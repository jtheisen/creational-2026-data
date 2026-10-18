"""Download and SHA-256-verify the completed October 2026 candidate pool."""
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib
import json
from pathlib import Path
import time
import urllib.request


SOURCE = ('https://creational.blob.core.windows.net/wikimedia-extracts/'
          'enwiki/2026-10-01/candidate-pool-v1/')
DEST = Path('data/candidate-pool-v1')
ARTIFACTS = ('pool.parquet', 'dependencies.parquet', 'redirects.json', 'report.json')


def fetch(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read()


def verified(path, descriptor):
    if not path.is_file() or path.stat().st_size != descriptor['bytes']:
        return False
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        while chunk := stream.read(4 * 1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest() == descriptor['sha256']


def download(url, path, descriptor):
    path.parent.mkdir(parents=True, exist_ok=True)
    if verified(path, descriptor):
        return 'present', path
    if path.exists():
        raise ValueError(f'Existing file failed verification: {path}')
    partial = path.with_name(path.name + '.partial')
    for attempt in range(1, 4):
        try:
            digest = hashlib.sha256()
            length = 0
            with urllib.request.urlopen(url, timeout=120) as response, partial.open('wb') as target:
                while chunk := response.read(4 * 1024 * 1024):
                    target.write(chunk)
                    digest.update(chunk)
                    length += len(chunk)
            if length != descriptor['bytes'] or digest.hexdigest() != descriptor['sha256']:
                raise ValueError(f'Download checksum/length mismatch: {url}')
            partial.rename(path)
            return 'downloaded', path
        except Exception:
            partial.unlink(missing_ok=True)
            if attempt == 3:
                raise
            time.sleep(attempt * 2)


def save_metadata(name, data):
    path = DEST / name
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and path.read_bytes() != data:
        raise ValueError(f'Existing metadata differs: {path}')
    if not path.exists():
        path.write_bytes(data)


def main():
    run_data = fetch(SOURCE + '_RUN.json')
    complete_data = fetch(SOURCE + '_COMPLETE.json')
    run = json.loads(run_data)
    complete = json.loads(complete_data)
    files = run['files']
    if (complete.get('phase') != 'complete' or len(files) != 19 or
            complete.get('files') != len(files) or
            complete.get('fingerprint') != run.get('fingerprint')):
        raise ValueError('Candidate pool is not a matching completed 19-shard run')
    tasks = []
    markers = []
    for item in files:
        stem = item['name'].removesuffix('.xml.bz2')
        marker_data = fetch(SOURCE + stem + '/_COMPLETE.json')
        marker = json.loads(marker_data)
        if (marker.get('fingerprint') != run['fingerprint'] or
                marker.get('source_sha256') != item['sha256'] or
                set(marker.get('artifacts', {})) != set(ARTIFACTS)):
            raise ValueError(f'Invalid shard completion marker: {stem}')
        markers.append((stem, marker_data))
        for name in ARTIFACTS:
            tasks.append((SOURCE + stem + '/' + name, DEST / stem / name,
                          marker['artifacts'][name]))
    save_metadata('_RUN.json', run_data)
    save_metadata('_COMPLETE.json', complete_data)
    save_metadata('aliases-discovered.json', fetch(SOURCE + 'aliases-discovered.json'))
    for stem, data in markers:
        save_metadata(stem + '/_COMPLETE.json', data)
    with ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(download, *task) for task in tasks]
        for number, future in enumerate(as_completed(futures), 1):
            state, path = future.result()
            print(f'{number}/{len(tasks)} {state}: {path}', flush=True)
    print(f'Complete: {len(files)} shards, {len(tasks)} verified artifacts', flush=True)


if __name__ == '__main__':
    main()
