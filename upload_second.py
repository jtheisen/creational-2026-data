"""Upload the already downloaded second dump file and measure Azure-only time."""
import base64
import hashlib
import json
import time
import urllib.request
import xml.etree.ElementTree as ET

from stream_dump import BLOCK_SIZE, put, token

NAME = 'enwiki-2026-10-01-p1113636p3871530.xml.bz2'
DEST = 'https://creational.blob.core.windows.net/wikimedia-imports/enwiki/2026-10-01/' + NAME
EXPECTED_SIZE = 2807278393
EXPECTED_HASH = '9bdcb23ba5a93fffe0d16d762efe89fa42402779d521bfb6e0cb33a136c6bf00'


def main():
    started = time.monotonic()
    digest = hashlib.sha256()
    with open(NAME, 'rb') as source:
        while chunk := source.read(BLOCK_SIZE):
            digest.update(chunk)
        size = source.tell()
    if size != EXPECTED_SIZE or digest.hexdigest() != EXPECTED_HASH:
        raise RuntimeError('Local size/checksum mismatch; upload not started')
    print(json.dumps({'state': 'checksum_verified', 'seconds': time.monotonic() - started}), flush=True)
    access_token = token()
    started = time.monotonic()
    blocks = []
    total = 0
    with open(NAME, 'rb') as source:
        while chunk := source.read(BLOCK_SIZE):
            block_id = base64.b64encode(f'{len(blocks):08d}'.encode()).decode()
            put(DEST + '?comp=block&blockid=' + block_id, chunk, access_token)
            blocks.append(block_id)
            total += len(chunk)
            print(json.dumps({'state': 'uploading', 'bytes': total,
                              'seconds': round(time.monotonic() - started, 2)}), flush=True)
    root = ET.Element('BlockList')
    for block_id in blocks:
        ET.SubElement(root, 'Latest').text = block_id
    put(DEST + '?comp=blocklist', ET.tostring(root), token())
    elapsed = time.monotonic() - started
    req = urllib.request.Request(DEST, method='HEAD', headers={
        'Authorization': 'Bearer ' + token(), 'x-ms-version': '2023-11-03'})
    with urllib.request.urlopen(req, timeout=30) as response:
        if int(response.headers['Content-Length']) != EXPECTED_SIZE:
            raise RuntimeError('Committed blob length mismatch')
    print(json.dumps({'state': 'complete', 'url': DEST, 'bytes': total,
                      'upload_seconds': round(elapsed, 2),
                      'MB_per_second': round(total / elapsed / 1e6, 2),
                      'sha256': digest.hexdigest()}), flush=True)


if __name__ == '__main__':
    main()
