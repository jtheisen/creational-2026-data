"""One-time migration of compatible alias checkpoints after the redirect fix."""
from extract_blob_set import SOURCE, DEST, inventory, fingerprint, read_json, write_json

OLD = 'c7f07e42ef9b4e26e7f70849d8a78cc5394650816d0fd7c11846ed66f6371512'


def main():
    manifest, files = inventory()
    new = fingerprint(manifest)
    previous = read_json(DEST + '_RUN.json')
    if not previous or previous['fingerprint'] != OLD:
        raise RuntimeError('Unexpected previous run; migration aborted')
    if read_json(DEST + '_COMPLETE.json'):
        raise RuntimeError('Run already complete; migration aborted')
    for item in files:
        base = DEST + item['name'].removesuffix('.xml.bz2') + '/'
        if read_json(base + '_COMPLETE.json'):
            raise RuntimeError('Extraction already started; migration aborted')
    migrated = []
    for item in files:
        relative = '_indexes/' + item['name'] + '.json'
        cached = read_json(DEST + relative)
        if cached is None:
            continue
        if cached.get('source_sha256') != item['sha256'] or cached.get('fingerprint') not in (OLD, new):
            raise RuntimeError('Unexpected checkpoint: ' + item['name'])
        if cached['fingerprint'] == OLD:
            write_json(DEST + '_migrations/redirect-linear-fix/' + relative, cached)
            cached['fingerprint'] = new
            cached['migrated_from_fingerprint'] = OLD
            write_json(DEST + relative, cached)
        migrated.append(item['name'])
    record = dict(reason='Replace catastrophically backtracking redirect regex with equivalent linear scan',
                  previous_fingerprint=OLD, fingerprint=new, reused_index_shards=migrated)
    write_json(DEST + '_migrations/redirect-linear-fix/record.json', record)
    write_json(DEST + '_migrations/redirect-linear-fix/previous-run.json', previous)
    write_json(DEST + '_RUN.json', {**previous, 'fingerprint': new, 'redirect_fix': record})
    print(record, flush=True)


if __name__ == '__main__':
    main()
