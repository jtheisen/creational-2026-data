"""Unattended, restartable extraction of the pinned 19-blob English export."""
import argparse
import base64
import fcntl
import hashlib
import json
from pathlib import Path
import re
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

import mwparserfromhell
from copy_dump_set import azure, log
from stream_dump import token
from extract_lifeforms import extract, index_redirects, resolve_aliases

SOURCE = 'https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/'
DEST = 'https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/extract-v2/'
ARTIFACTS = ('candidates.ndjson.gz', 'dependencies.ndjson.gz', 'review.ndjson.gz', 'report.json')
BLOCK = 16 * 1024 * 1024


def read_bytes(url):
    for attempt in range(6):
        try:
            req = urllib.request.Request(url, headers={
                'Authorization': 'Bearer ' + token(), 'x-ms-version': '2023-11-03'})
            with urllib.request.urlopen(req, timeout=120) as response:
                return response.read()
        except urllib.error.HTTPError as exc:
            if exc.code not in (401, 408, 429, 500, 502, 503, 504) or attempt == 5:
                raise
        except OSError:
            if attempt == 5:
                raise
        time.sleep(min(2 ** attempt, 30))


def read_json(url):
    try:
        return json.loads(read_bytes(url))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def write_json(url, value):
    data = json.dumps(value, sort_keys=True).encode()
    checksum = hashlib.sha256(data).hexdigest()
    azure('PUT', url, data, {'x-ms-blob-type': 'BlockBlob',
                           'x-ms-blob-content-type': 'application/json',
                           'x-ms-meta-sha256': checksum})
    return dict(bytes=len(data), sha256=checksum)


def blob_matches(url, descriptor):
    try:
        h = {k.lower(): v for k, v in azure('HEAD', url).items()}
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return False
        raise
    return (int(h['content-length']) == descriptor['bytes'] and
            h.get('x-ms-meta-sha256') == descriptor['sha256'])


def upload(path, url):
    digest = hashlib.sha256()
    blocks = []
    total = 0
    session = uuid.uuid4().hex
    with path.open('rb') as stream:
        while chunk := stream.read(BLOCK):
            block = base64.b64encode(f'{session}-{len(blocks):08d}'.encode()).decode()
            azure('PUT', url + '?comp=block&blockid=' + urllib.parse.quote(block, safe=''), chunk)
            blocks.append(block)
            digest.update(chunk)
            total += len(chunk)
    root = ET.Element('BlockList')
    for block in blocks:
        ET.SubElement(root, 'Latest').text = block
    descriptor = dict(bytes=total, sha256=digest.hexdigest())
    azure('PUT', url + '?comp=blocklist', ET.tostring(root), {
        'x-ms-meta-sha256': descriptor['sha256'],
        'x-ms-blob-content-type': ('application/json' if path.suffix == '.json' else
                                   'application/vnd.apache.parquet' if path.suffix == '.parquet' else
                                   'application/gzip'),
    })
    if not blob_matches(url, descriptor):
        raise RuntimeError('Uploaded blob verification failed: ' + url)
    return descriptor


def completed_part(marker, fingerprint, checksum, base):
    if not marker or marker.get('fingerprint') != fingerprint or marker.get('source_sha256') != checksum:
        return False
    artifacts = marker.get('artifacts', {})
    return (set(artifacts) == set(ARTIFACTS) and
            all(blob_matches(base + name, artifacts[name]) for name in ARTIFACTS))


def inventory():
    raw = read_bytes(SOURCE + 'SHA256SUMS')
    source_complete = read_json(SOURCE + '_COMPLETE.json')
    if not source_complete or source_complete['files'] != 19:
        raise RuntimeError('Source import is not complete')
    files = []
    for line in raw.decode().splitlines():
        checksum, name = line.split()
        if not re.fullmatch(r'enwiki-2026-10-01-[\w]+\.xml\.bz2', name) or not re.fullmatch(r'[0-9a-f]{64}', checksum):
            raise ValueError('Invalid manifest entry')
        h = {k.lower(): v for k, v in azure('HEAD', SOURCE + name).items()}
        # The first two blobs predate SHA metadata; each pass verifies bytes anyway.
        if h.get('x-ms-meta-sha256', checksum) != checksum:
            raise ValueError('Source metadata differs from manifest: ' + name)
        files.append(dict(name=name, sha256=checksum, bytes=int(h['content-length'])))
    if len(files) != 19 or len({f['name'] for f in files}) != 19 or sum(f['bytes'] for f in files) != source_complete['bytes']:
        raise ValueError('Source inventory differs from completed import')
    return raw, files


def fingerprint(manifest):
    digest = hashlib.sha256(manifest)
    for filename in ('extract_blob_set.py', 'extract_lifeforms.py', 'copy_dump_set.py', 'stream_dump.py'):
        digest.update(Path(__file__).with_name(filename).read_bytes())
    digest.update(mwparserfromhell.__version__.encode())
    return digest.hexdigest()


def retry(label, operation):
    for attempt in range(1, 4):
        try:
            return operation()
        except Exception as exc:
            log('task_error', task=label, attempt=attempt, error=str(exc))
            if attempt == 3:
                raise
            time.sleep(60 * attempt)


def run(check=False):
    with open('extract_blob_set.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        manifest, files = inventory()
        signature = fingerprint(manifest)
        previous = read_json(DEST + '_RUN.json')
        if previous and previous.get('fingerprint') != signature:
            raise RuntimeError('Destination belongs to different code/input; use a new version prefix')
        probe = DEST + '_access-check-' + uuid.uuid4().hex + '.json'
        write_json(probe, {'access': 'check'})
        azure('DELETE', probe)
        log('preflight_complete', files=len(files), bytes=sum(f['bytes'] for f in files), destination=DEST,
            fingerprint=signature)
        if check:
            return
        started = time.monotonic()
        write_json(DEST + '_RUN.json', dict(fingerprint=signature, files=files))
        write_json(DEST + '_STATUS.json', dict(phase='index_aliases', fingerprint=signature))
        redirects = {}
        # Shared indexing prevents alias definitions in later shards being missed.
        for number, item in enumerate(files, 1):
            name, checksum = item['name'], item['sha256']
            index_url = DEST + '_indexes/' + name + '.json'
            cached = read_json(index_url)
            if not cached or cached.get('fingerprint') != signature or cached.get('source_sha256') != checksum:
                def build_index():
                    log('index_start', file=name, number=number, total=len(files))
                    found = index_redirects(SOURCE + name, checksum)
                    value = dict(fingerprint=signature, source_sha256=checksum, redirects=found)
                    write_json(index_url, value)
                    return value
                cached = retry('index:' + name, build_index)
                log('index_complete', file=name, redirects=len(cached['redirects']))
            else:
                log('index_skipped', file=name)
            redirects.update(cached['redirects'])
            write_json(DEST + '_STATUS.json', dict(phase='index_aliases', completed=number, total=len(files), file=name))
        aliases = resolve_aliases(redirects)
        write_json(DEST + 'aliases.json', dict(fingerprint=signature, aliases=aliases))
        summaries = []
        for number, item in enumerate(files, 1):
            name, checksum = item['name'], item['sha256']
            base = DEST + name.removesuffix('.xml.bz2') + '/'
            marker = read_json(base + '_COMPLETE.json')
            if completed_part(marker, signature, checksum, base):
                log('shard_skipped', file=name)
            else:
                def process():
                    log('extraction_start', file=name, number=number, total=len(files))
                    # Only compressed extracts use disk; scratch is removed on success/failure.
                    with tempfile.TemporaryDirectory(prefix='wikimedia-extract-') as scratch:
                        report = extract(SOURCE + name, scratch, aliases=aliases,
                                         expected_sha256=checksum, source_bytes=item['bytes'])
                        artifacts = {}
                        for artifact in ARTIFACTS:
                            artifacts[artifact] = upload(Path(scratch) / artifact, base + artifact)
                        value = dict(fingerprint=signature, source_sha256=checksum, artifacts=artifacts,
                                     counts={k: report[k] for k in ('pages_scanned', 'candidates', 'dependencies', 'review_candidates')})
                        write_json(base + '_COMPLETE.json', value)
                        return value
                marker = retry('extract:' + name, process)
                log('shard_complete', file=name, **marker['counts'])
            summaries.append(dict(source=name, **marker['counts']))
            write_json(DEST + '_STATUS.json', dict(phase='extract', completed=number, total=len(files), file=name))
        # Recheck stored artifact metadata for every shard before publishing completion.
        for item in files:
            base = DEST + item['name'].removesuffix('.xml.bz2') + '/'
            if not completed_part(read_json(base + '_COMPLETE.json'), signature, item['sha256'], base):
                raise RuntimeError('Final verification failed: ' + item['name'])
        result = dict(phase='complete', fingerprint=signature, files=len(files), shards=summaries,
                      seconds=round(time.monotonic() - started, 1))
        write_json(DEST + '_COMPLETE.json', result)
        write_json(DEST + '_STATUS.json', result)
        log('complete', files=len(files), seconds=result['seconds'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Validate inventory and read/write/delete access, without extracting')
    args = parser.parse_args()
    run(args.check)
