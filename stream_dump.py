"""Stream one pinned Wikimedia dump into Azure block storage using VM identity."""
import base64
import hashlib
import json
import subprocess
import time
import urllib.request
import xml.etree.ElementTree as ET

NAME = 'enwiki-2026-10-01-p10p1113635.xml.bz2'
SOURCE = 'https://dumps.wikimedia.org/other/mediawiki_content_current/enwiki/2026-10-01/xml/bzip2/' + NAME
DEST = 'https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/' + NAME
EXPECTED_HASH = '400d2b24b9304c1b620d7db98c9514e354411b68eb0305c8b96f1a7cf3ca7ca6'
EXPECTED_SIZE = 2694239210
BLOCK_SIZE = 16 * 1024 * 1024


def token():
    url = 'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fstorage.azure.com%2F'
    req = urllib.request.Request(url, headers={'Metadata': 'true'})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.load(response)['access_token']


def put(url, data, access_token):
    for attempt in range(5):
        try:
            req = urllib.request.Request(url, data=data, method='PUT', headers={
                'Authorization': 'Bearer ' + access_token,
                'x-ms-version': '2023-11-03',
                'Content-Type': 'application/octet-stream',
            })
            with urllib.request.urlopen(req, timeout=180) as response:
                return response.status
        except Exception:
            if attempt == 4:
                raise
            time.sleep(2 ** attempt)


def main():
    access_token = token()
    digest = hashlib.sha256()
    blocks = []
    total = 0
    started = time.monotonic()
    # curl handles the upstream HTTP connection; no dump bytes are written to disk.
    proc = subprocess.Popen(['curl', '--fail', '--location', '--silent', '--show-error',
                             '--connect-timeout', '30', '--speed-limit', '1024',
                             '--speed-time', '180', '--user-agent',
                             'CreationalWikipediaImport/0.1 (single-file research import)',
                             SOURCE], stdout=subprocess.PIPE)
    try:
        while True:
            chunk = proc.stdout.read(BLOCK_SIZE)
            if not chunk:
                break
            block_id = base64.b64encode(f'{len(blocks):08d}'.encode()).decode()
            put(DEST + '?comp=block&blockid=' + block_id, chunk, access_token)
            blocks.append(block_id)
            digest.update(chunk)
            total += len(chunk)
            print(json.dumps({'state': 'streaming', 'bytes': total,
                              'expected_bytes': EXPECTED_SIZE,
                              'elapsed_seconds': round(time.monotonic() - started, 1)}), flush=True)
        if proc.wait() != 0:
            raise RuntimeError('Source download failed; blob not committed')
        if total != EXPECTED_SIZE or digest.hexdigest() != EXPECTED_HASH:
            raise RuntimeError('Size/checksum mismatch; blob not committed')
        root = ET.Element('BlockList')
        for block_id in blocks:
            ET.SubElement(root, 'Latest').text = block_id
        put(DEST + '?comp=blocklist', ET.tostring(root), token())
        req = urllib.request.Request(DEST, method='HEAD', headers={
            'Authorization': 'Bearer ' + token(), 'x-ms-version': '2023-11-03'})
        with urllib.request.urlopen(req, timeout=30) as response:
            if int(response.headers['Content-Length']) != EXPECTED_SIZE:
                raise RuntimeError('Committed blob length mismatch')
        print(json.dumps({'state': 'complete', 'url': DEST, 'bytes': total,
                          'sha256': digest.hexdigest()}), flush=True)
    finally:
        if proc.poll() is None:
            proc.terminate()
        proc.stdout.close()
        proc.wait()


if __name__ == '__main__':
    main()
