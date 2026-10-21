"""Stream a pinned MediaWiki image-table SQL dump into a compact Parquet catalog."""

import re
import zlib
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq


COLUMNS = (
    'img_name', 'img_size', 'img_width', 'img_height', 'img_metadata',
    'img_bits', 'img_media_type', 'img_major_mime', 'img_minor_mime',
    'img_description_id', 'img_actor', 'img_timestamp', 'img_sha1',
)
SCHEMA = pa.schema([
    ('source_wiki', pa.string()), ('file_title', pa.string()), ('canonical_key', pa.string()),
    ('original_bytes', pa.int64()), ('width', pa.int32()), ('height', pa.int32()),
    ('media_type', pa.string()), ('mime', pa.string()), ('uploaded_at', pa.string()),
    ('sha1_base36', pa.string()),
])
ESCAPES = {ord('0'): 0, ord('b'): 8, ord('n'): 10, ord('r'): 13,
           ord('t'): 9, ord('Z'): 26}


def canonical_key(title: str) -> str:
    if title.lower().startswith('file:'):
        title = title[5:]
    return title.replace('_', ' ').strip().casefold()


def row_key(line: bytes) -> str:
    """Read only img_name, leaving the potentially large metadata field untouched."""
    if not line.startswith(b"('"):
        raise ValueError('Expected quoted image filename')
    position = 2
    name = bytearray()
    while position < len(line):
        char = line[position]
        position += 1
        if char == 92:
            if position >= len(line):
                raise ValueError('Truncated filename escape')
            escaped = line[position]
            position += 1
            name.append(ESCAPES.get(escaped, escaped))
        elif char == 39:
            if position < len(line) and line[position] == 39:
                name.append(39)
                position += 1
            else:
                return canonical_key(name.decode('utf-8', errors='replace'))
        else:
            name.append(char)
    raise ValueError('Unterminated filename')


def _fields(line: bytes) -> list[bytes | None]:
    line = line.strip()
    if not line.startswith(b'('):
        raise ValueError('Expected SQL image row')
    fields = []
    position = 1
    while position < len(line):
        if line[position] == 39:
            position += 1
            value = bytearray()
            while position < len(line):
                char = line[position]
                position += 1
                if char == 92:
                    if position >= len(line):
                        raise ValueError('Truncated SQL escape')
                    escaped = line[position]
                    position += 1
                    value.append(ESCAPES.get(escaped, escaped))
                elif char == 39:
                    if position < len(line) and line[position] == 39:
                        value.append(39)
                        position += 1
                    else:
                        break
                else:
                    value.append(char)
            else:
                raise ValueError('Unterminated SQL string')
            fields.append(bytes(value))
        else:
            end = position
            while end < len(line) and line[end] not in (44, 41):
                end += 1
            token = line[position:end].strip()
            fields.append(None if token == b'NULL' else token)
            position = end
        if position >= len(line):
            raise ValueError('Truncated SQL row')
        if line[position] == 41:
            if len(fields) != len(COLUMNS):
                raise ValueError(f'Expected {len(COLUMNS)} image fields, got {len(fields)}')
            return fields
        if line[position] != 44:
            raise ValueError('Expected SQL field separator')
        position += 1
    raise ValueError('Truncated SQL row')


def parse_image_row(line: bytes) -> dict:
    fields = _fields(line)
    def decoded(index):
        return fields[index].decode('utf-8', errors='replace') if fields[index] is not None else None
    name = decoded(0).replace('_', ' ')
    return {
        'file_title': 'File:' + name,
        'canonical_key': canonical_key(name),
        'original_bytes': int(fields[1]), 'width': int(fields[2]), 'height': int(fields[3]),
        'media_type': decoded(6),
        'mime': decoded(7) + '/' + decoded(8),
        'uploaded_at': decoded(11), 'sha1_base36': decoded(12),
    }


def _lines_from_gzip(chunks):
    inflater = zlib.decompressobj(16 + zlib.MAX_WBITS)
    pending = b''
    for chunk in chunks:
        data = inflater.decompress(chunk)
        lines = (pending + data).split(b'\n')
        pending = lines.pop()
        for line in lines:
            yield line.rstrip(b'\r')
    if not inflater.eof:
        raise ValueError('truncated gzip image dump')
    pending += inflater.flush()
    if pending:
        yield pending


def convert_image_sql(chunks, output: Path, wiki: str, candidates: set[str] | None = None) -> dict:
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    columns = []
    in_insert = False
    seen_insert = False
    scanned = retained = 0
    rows = []
    writer = None
    try:
        for line in _lines_from_gzip(chunks):
            if line.startswith(b'  `img_') and not seen_insert:
                columns.append(line.split(b'`', 2)[1].decode('ascii'))
            if line.startswith(b'INSERT INTO `image` VALUES'):
                if tuple(columns) != COLUMNS:
                    raise ValueError('Unexpected image SQL column order')
                in_insert = seen_insert = True
                continue
            if not in_insert or not line.startswith(b'('):
                continue
            scanned += 1
            if candidates is not None and row_key(line) not in candidates:
                continue
            row = parse_image_row(line)
            rows.append({'source_wiki': wiki, **row})
            retained += 1
            if len(rows) >= 20000:
                writer = writer or pq.ParquetWriter(output, SCHEMA, compression='snappy')
                writer.write_table(pa.Table.from_pylist(rows, schema=SCHEMA))
                rows.clear()
            if line.rstrip().endswith(b';'):
                in_insert = False
        if not seen_insert:
            raise ValueError('Image SQL dump contains no INSERT statements')
        writer = writer or pq.ParquetWriter(output, SCHEMA, compression='snappy')
        if rows:
            writer.write_table(pa.Table.from_pylist(rows, schema=SCHEMA))
        return {'source_wiki': wiki, 'scanned': scanned, 'retained': retained,
                'candidate_count': None if candidates is None else len(candidates)}
    finally:
        if writer is not None:
            writer.close()
