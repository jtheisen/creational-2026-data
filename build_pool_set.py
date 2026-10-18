"""Unattended, restartable one-pass candidate-pool build for 19 Azure blobs."""
import argparse
import fcntl
import hashlib
import json
from pathlib import Path
import tempfile
import time
import uuid

import pyarrow

from build_candidate_pool import build_pool, check_aliases
from copy_dump_set import azure, log
from extract_blob_set import SOURCE, blob_matches, inventory, read_json, retry, upload, write_json
from extract_lifeforms import resolve_aliases

DEST = 'https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/candidate-pool-v1/'
SEED = 'https://creational.blob.core.windows.net/wikimedia-extracts/enwiki/2026-10-01/extract-v2/aliases.json'
ARTIFACTS = ('pool.parquet', 'dependencies.parquet', 'redirects.json', 'report.json')


def fingerprint(manifest, aliases):
    digest = hashlib.sha256(manifest)
    digest.update(json.dumps(aliases, sort_keys=True).encode())
    for filename in ('build_pool_set.py', 'build_candidate_pool.py', 'extract_lifeforms.py',
                     'extract_blob_set.py', 'copy_dump_set.py', 'stream_dump.py'):
        digest.update(Path(__file__).with_name(filename).read_bytes())
    digest.update(pyarrow.__version__.encode())
    return digest.hexdigest()


def completed_part(marker, signature, checksum, base):
    if not marker or marker.get('fingerprint') != signature or marker.get('source_sha256') != checksum:
        return False
    artifacts = marker.get('artifacts', {})
    return set(artifacts) == set(ARTIFACTS) and all(
        blob_matches(base + name, artifacts[name]) for name in ARTIFACTS)


def run(check=False):
    with open('build_pool_set.lock', 'w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        manifest, files = inventory()
        seed_record = read_json(SEED)
        if not seed_record or not isinstance(seed_record.get('aliases'), dict):
            raise RuntimeError('Existing complete-export alias seed is unavailable')
        aliases = seed_record['aliases']
        signature = fingerprint(manifest, aliases)
        previous = read_json(DEST + '_RUN.json')
        if previous and previous.get('fingerprint') != signature:
            raise RuntimeError('Destination belongs to different code/input; use a new version prefix')
        probe = DEST + '_access-check-' + uuid.uuid4().hex + '.json'
        write_json(probe, {'access': 'check'})
        azure('DELETE', probe)
        log('preflight_complete', files=len(files), bytes=sum(item['bytes'] for item in files),
            aliases=len(aliases), fingerprint=signature, destination=DEST)
        if check:
            return
        started = time.monotonic()
        write_json(DEST + '_RUN.json', dict(fingerprint=signature, files=files,
                                           seed=SEED, aliases=aliases))
        summaries = []
        redirects = {}
        for number, item in enumerate(files, 1):
            name, checksum = item['name'], item['sha256']
            base = DEST + name.removesuffix('.xml.bz2') + '/'
            marker = read_json(base + '_COMPLETE.json')
            if completed_part(marker, signature, checksum, base):
                log('shard_skipped', file=name)
            else:
                def process():
                    log('pool_start', file=name, number=number, total=len(files))
                    with tempfile.TemporaryDirectory(prefix='wikimedia-pool-') as scratch:
                        report = build_pool(SOURCE + name, scratch, aliases, checksum, item['bytes'])
                        artifacts = {artifact: upload(Path(scratch) / artifact, base + artifact)
                                     for artifact in ARTIFACTS}
                        value = dict(fingerprint=signature, source_sha256=checksum,
                                     artifacts=artifacts, counts={key: report[key] for key in
                                         ('pages_scanned', 'pool_pages', 'dependencies', 'template_redirects')})
                        write_json(base + '_COMPLETE.json', value)
                        return value
                marker = retry('pool:' + name, process)
                log('shard_complete', file=name, **marker['counts'])
            summaries.append(dict(source=name, **marker['counts']))
            redirects.update(read_json(base + 'redirects.json'))
            write_json(DEST + '_STATUS.json', dict(phase='pool', completed=number,
                                                  total=len(files), file=name,
                                                  pool_pages=sum(s['pool_pages'] for s in summaries)))
        discovered = resolve_aliases(redirects)
        write_json(DEST + 'aliases-discovered.json', dict(fingerprint=signature, aliases=discovered))
        try:
            check_aliases(aliases, discovered)
        except ValueError as exc:
            write_json(DEST + '_STATUS.json', dict(phase='alias_mismatch', error=str(exc),
                                                  completed=len(files), total=len(files)))
            raise
        for item in files:
            base = DEST + item['name'].removesuffix('.xml.bz2') + '/'
            if not completed_part(read_json(base + '_COMPLETE.json'), signature, item['sha256'], base):
                raise RuntimeError('Final verification failed: ' + item['name'])
        result = dict(phase='complete', fingerprint=signature, files=len(files),
                      shards=summaries, pool_pages=sum(s['pool_pages'] for s in summaries),
                      seconds=round(time.monotonic() - started, 1))
        write_json(DEST + '_COMPLETE.json', result)
        write_json(DEST + '_STATUS.json', result)
        log('complete', files=len(files), pool_pages=result['pool_pages'], seconds=result['seconds'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    run(parser.parse_args().check)
