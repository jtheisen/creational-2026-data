"""Mirror the pinned English current export without storing dump files locally.

Run with --check for a read-only inventory. Re-running skips verified blobs.
Uses only Python's standard library and curl; authenticates via VM identity.
"""
import argparse
import base64
import fcntl
import hashlib
import json
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET

from stream_dump import token

SOURCE = 'https://dumps.wikimedia.org/other/mediawiki_content_current/enwiki/2026-10-01/xml/bzip2/'
DEST = 'https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/'
BLOCK_SIZE = 16 * 1024 * 1024
# These two blobs were checksum-verified by the earlier single-file transfers.
PREVERIFIED = {
    'enwiki-2026-10-01-p10p1113635.xml.bz2': '400d2b24b9304c1b620d7db98c9514e354411b68eb0305c8b96f1a7cf3ca7ca6',
    'enwiki-2026-10-01-p1113636p3871530.xml.bz2': '9bdcb23ba5a93fffe0d16d762efe89fa42402779d521bfb6e0cb33a136c6bf00',
}
_token = None
_token_time = 0


def log(state, **fields):
    print(json.dumps(dict(time=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
                          state=state, **fields)), flush=True)


def azure(method, url, data=None, headers=None):
    global _token, _token_time
    for attempt in range(6):
        try:
            if _token is None or time.monotonic() - _token_time > 1200:
                _token = token()
                _token_time = time.monotonic()
            req = urllib.request.Request(url, data=data, method=method, headers={
                'Authorization': 'Bearer ' + _token, 'x-ms-version': '2023-11-03',
                **(headers or {}),
            })
            with urllib.request.urlopen(req, timeout=120) as response:
                return dict(response.headers.items())
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                raise
            if exc.code == 401:
                _token = None
            if exc.code not in (401, 408, 429, 500, 502, 503, 504):
                raise
            if attempt == 5:
                raise
        except (OSError, TimeoutError):
            if attempt == 5:
                raise
        time.sleep(min(2 ** attempt, 30))


def fetch(url):
    return subprocess.check_output([
        'curl', '--fail', '--location', '--silent', '--show-error',
        '--retry', '5', '--connect-timeout', '30', '--max-time', '120',
        '--user-agent', 'CreationalWikipediaImport/0.1 (research dump mirror)', url,
    ]).decode()


def inventory():
    manifest = fetch(SOURCE + 'SHA256SUMS')
    index = fetch(SOURCE)
    sizes = {name: int(size) for name, size in re.findall(
        r'href="([^"]+\.xml\.bz2)"[^\n]*?\s(\d+)\s*$', index, re.M)}
    files = []
    for line in manifest.splitlines():
        checksum, name = line.split()
        if not re.fullmatch(r'enwiki-2026-10-01-[\w]+\.xml\.bz2', name):
            raise ValueError('Unexpected manifest filename: ' + name)
        if not re.fullmatch(r'[0-9a-f]{64}', checksum) or name not in sizes:
            raise ValueError('Invalid or incomplete source inventory')
        files.append((name, sizes[name], checksum))
    if len(files) != 19 or len({name for name, _, _ in files}) != 19:
        raise ValueError('Expected 19 unique source files')
    return manifest, files


def complete(name, size, checksum):
    try:
        headers = {k.lower(): v for k, v in azure('HEAD', DEST + name).items()}
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return False
        raise
    return (int(headers['content-length']) == size and
            (headers.get('x-ms-meta-sha256') == checksum or
             PREVERIFIED.get(name) == checksum))


def transfer(name, size, checksum):
    url = DEST + name
    session = uuid.uuid4().hex
    blocks = []
    digest = hashlib.sha256()
    total = 0
    started = time.monotonic()
    proc = subprocess.Popen([
        'curl', '--fail', '--location', '--silent', '--show-error',
        '--connect-timeout', '30', '--speed-limit', '1024', '--speed-time', '180',
        '--max-time', '7200', '--user-agent',
        'CreationalWikipediaImport/0.1 (research dump mirror)', SOURCE + name,
    ], stdout=subprocess.PIPE)
    try:
        while chunk := proc.stdout.read(BLOCK_SIZE):
            block = base64.b64encode(f'{session}-{len(blocks):08d}'.encode()).decode()
            azure('PUT', url + '?comp=block&blockid=' + urllib.parse.quote(block, safe=''), chunk)
            blocks.append(block)
            total += len(chunk)
            digest.update(chunk)
            if total > size:
                raise ValueError('Source larger than inventory')
            log('progress', file=name, bytes=total, expected_bytes=size,
                seconds=round(time.monotonic() - started, 1))
        if proc.wait() != 0:
            raise RuntimeError('curl failed')
        if total != size or digest.hexdigest() != checksum:
            raise ValueError('Source size/checksum mismatch; not committing blob')
        root = ET.Element('BlockList')
        for block in blocks:
            ET.SubElement(root, 'Latest').text = block
        azure('PUT', url + '?comp=blocklist', ET.tostring(root), {
            'x-ms-meta-sha256': checksum,
            'x-ms-blob-content-type': 'application/octet-stream',
        })
        if not complete(name, size, checksum):
            raise RuntimeError('Committed blob verification failed')
        log('file_complete', file=name, bytes=total, sha256=checksum,
            seconds=round(time.monotonic() - started, 1))
    finally:
        if proc.poll() is None:
            proc.terminate()
        proc.stdout.close()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    with open('copy_dump_set.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        started = time.monotonic()
        manifest, files = inventory()
        pending = []
        for name, size, checksum in files:
            exists = complete(name, size, checksum)
            log('present' if exists else 'pending', file=name, bytes=size)
            if not exists:
                pending.append((name, size, checksum))
        log('inventory', files=len(files), pending=len(pending),
            total_bytes=sum(s for _, s, _ in files),
            pending_bytes=sum(s for _, s, _ in pending))
        if args.check:
            return
        azure('PUT', DEST + 'SHA256SUMS', manifest.encode(), {'x-ms-blob-type': 'BlockBlob'})
        failures = []
        for name, size, checksum in pending:
            for attempt in range(1, 6):
                try:
                    log('file_start', file=name, attempt=attempt)
                    transfer(name, size, checksum)
                    break
                except Exception as exc:
                    log('file_error', file=name, attempt=attempt, error=str(exc))
                    if attempt == 5:
                        failures.append(name)
                    else:
                        time.sleep(min(30 * 2 ** (attempt - 1), 300))
        if failures:
            raise RuntimeError('Failed files: ' + ', '.join(failures))
        for name, size, checksum in files:
            if not complete(name, size, checksum):
                raise RuntimeError('Final inventory verification failed: ' + name)
        result = dict(files=len(files), bytes=sum(s for _, s, _ in files),
                      seconds=round(time.monotonic() - started, 1))
        azure('PUT', DEST + '_COMPLETE.json', json.dumps(result).encode(),
              {'x-ms-blob-type': 'BlockBlob'})
        log('complete', **result)


if __name__ == '__main__':
    main()
