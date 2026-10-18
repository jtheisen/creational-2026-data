"""Write one Wikimedia bzip2 XML shard to a Snappy Parquet file."""
import argparse
import json
import time
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

from extract_lifeforms import pages


SCHEMA = pa.schema([
    ('page_id', pa.int64()),
    ('title', pa.string()),
    ('namespace', pa.int32()),
    ('revision_id', pa.int64()),
    ('timestamp', pa.string()),
    ('text', pa.string()),
])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--batch-rows', type=int, default=5000)
    args = parser.parse_args()

    if args.output.exists() or args.output.with_suffix(args.output.suffix + '.partial').exists():
        raise FileExistsError('Choose a fresh output path; existing files are never overwritten')

    partial = args.output.with_suffix(args.output.suffix + '.partial')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    columns = {field.name: [] for field in SCHEMA}
    count = 0
    writer = pq.ParquetWriter(partial, SCHEMA, compression='snappy', use_dictionary=['title'])
    try:
        for page in pages(args.source):
            columns['page_id'].append(page['page_id'])
            columns['title'].append(page['title'])
            columns['namespace'].append(page['namespace'])
            columns['revision_id'].append(page['revision_id'])
            columns['timestamp'].append(page['timestamp'])
            columns['text'].append(page['wikitext'])
            count += 1
            if len(columns['page_id']) >= args.batch_rows:
                writer.write_table(pa.Table.from_pydict(columns, schema=SCHEMA), row_group_size=50000)
                columns = {field.name: [] for field in SCHEMA}
                if count % 100000 == 0:
                    print(json.dumps({'pages': count, 'seconds': round(time.monotonic() - started, 1)}), flush=True)
        if columns['page_id']:
            writer.write_table(pa.Table.from_pydict(columns, schema=SCHEMA), row_group_size=50000)
    except BaseException:
        writer.close()
        partial.unlink(missing_ok=True)
        raise
    else:
        writer.close()

    partial.replace(args.output)
    metadata = pq.ParquetFile(args.output).metadata
    print(json.dumps({
        'output': str(args.output),
        'pages': count,
        'row_groups': metadata.num_row_groups,
        'compression': metadata.row_group(0).column(0).compression if count else None,
        'bytes': args.output.stat().st_size,
        'seconds': round(time.monotonic() - started, 2),
    }, indent=2), flush=True)


if __name__ == '__main__':
    main()
