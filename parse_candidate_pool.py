"""Confirm taxoboxes and collect raw parameters and image references from Parquet."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import time

import mwparserfromhell as mw
import pyarrow as pa
import pyarrow.parquet as pq

from extract_lifeforms import normalize


SOURCE_ROOT = Path('data/candidate-pool-v1')
OUTPUT_ROOT = Path('results/candidate-parse-v1')
IMAGE_FIELD = re.compile(
    r'^(?:image\d*|range[_ ]map\d*|(?:subdivision|synonyms|fossil[_ ]range)[_ ]image)$', re.I)
FILE_PREFIX = re.compile(r'^(?:File|Image)\s*:\s*', re.I)
FILE_LINK = re.compile(r'^\s*(?:File|Image)\s*:', re.I)
ARTIFACTS = ('articles.parquet', 'taxoboxes.parquet',
             'taxobox_parameters.parquet', 'image_references.parquet')
SCHEMAS = {
    'articles': pa.schema([
        ('page_id', pa.int64()), ('revision_id', pa.int64()), ('title', pa.string()),
        ('timestamp', pa.string()), ('source_file', pa.string()),
        ('selection_signal', pa.string()), ('taxobox_count', pa.int32()),
        ('has_taxonbar', pa.bool_()), ('section_only', pa.bool_()),
        ('location_unresolved_count', pa.int32()), ('review_reason', pa.string()),
    ]),
    'taxoboxes': pa.schema([
        ('page_id', pa.int64()), ('revision_id', pa.int64()),
        ('box_index', pa.int32()), ('family', pa.string()),
        ('template_name', pa.string()), ('raw_markup', pa.large_string()),
        ('start', pa.int64()), ('end', pa.int64()),
        ('section_path', pa.list_(pa.string())), ('is_top_level', pa.bool_()),
    ]),
    'taxobox_parameters': pa.schema([
        ('page_id', pa.int64()), ('revision_id', pa.int64()),
        ('box_index', pa.int32()), ('parameter_index', pa.int32()),
        ('raw_name', pa.string()), ('normalized_name', pa.string()),
        ('raw_value', pa.large_string()), ('show_key', pa.bool_()),
    ]),
    'image_references': pa.schema([
        ('page_id', pa.int64()), ('revision_id', pa.int64()),
        ('reference_index', pa.int32()), ('source_kind', pa.string()),
        ('box_index', pa.int32()), ('parameter_index', pa.int32()),
        ('raw_name', pa.string()), ('raw_value', pa.large_string()),
        ('file_title', pa.string()),
    ]),
}


def file_title(raw, allow_bare=True):
    value = raw.strip()
    if not value or any(markup in value for markup in ('{{', '}}', '[[', ']]', '<', '>', '|', '\n')):
        return None
    if value.casefold() in ('none', 'no', 'yes', 'unknown', '-'):
        return None
    prefixed = bool(FILE_PREFIX.match(value))
    if not prefixed and not allow_bare:
        return None
    name = FILE_PREFIX.sub('', value).strip()
    return 'File:' + name if name else None


def section_at(headings, position):
    if position is None:
        return []
    stack = []
    for offset, level, title in headings:
        if offset >= position:
            break
        while stack and stack[-1][0] >= level:
            stack.pop()
        stack.append((level, title))
    return [title for _, title in stack]


def parse_page(page, aliases):
    wikitext = page['wikitext']
    code = mw.parse(wikitext, skip_style_tags=True)
    headings = []
    offset = 0
    for node in code.nodes:
        if isinstance(node, mw.nodes.Heading):
            headings.append((offset, node.level, str(node.title).strip()))
        offset += len(str(node))
    top = {id(template) for template in code.filter_templates(recursive=False)}
    templates = code.filter_templates()
    boxes = []
    parameters = []
    images = []
    cursor = 0
    has_taxonbar = False

    def add_image(source_kind, raw_value, raw_name=None, box_index=None,
                  parameter_index=None, title=None):
        images.append(dict(page_id=page['page_id'], revision_id=page['revision_id'],
                           reference_index=len(images), source_kind=source_kind,
                           box_index=box_index, parameter_index=parameter_index,
                           raw_name=raw_name, raw_value=raw_value, file_title=title))

    for template in templates:
        name = normalize(str(template.name))
        if name == 'taxonbar':
            has_taxonbar = True
        if name not in aliases:
            continue
        box_index = len(boxes)
        raw = str(template)
        start = wikitext.find(raw, cursor)
        if start >= 0:
            cursor = start + 1
            end = start + len(raw)
        else:
            start = end = None
        boxes.append(dict(page_id=page['page_id'], revision_id=page['revision_id'],
                          box_index=box_index, family=aliases[name],
                          template_name=str(template.name), raw_markup=raw,
                          start=start, end=end, section_path=section_at(headings, start),
                          is_top_level=id(template) in top))
        for parameter_index, param in enumerate(template.params):
            raw_name = str(param.name)
            raw_value = str(param.value)
            parameters.append(dict(page_id=page['page_id'], revision_id=page['revision_id'],
                                   box_index=box_index, parameter_index=parameter_index,
                                   raw_name=raw_name, normalized_name=normalize(raw_name),
                                   raw_value=raw_value, show_key=bool(param.showkey)))
            if IMAGE_FIELD.fullmatch(normalize(raw_name)) and raw_value.strip():
                add_image('taxobox_parameter', raw_value, raw_name=raw_name,
                          box_index=box_index, parameter_index=parameter_index,
                          title=file_title(raw_value))

    for link in code.filter_wikilinks():
        title = str(link.title)
        if FILE_LINK.match(title):
            add_image('file_link', str(link), title=file_title(title, allow_bare=False))
    for tag in code.filter_tags():
        if str(tag.tag).strip().casefold() != 'gallery' or tag.contents is None:
            continue
        for line in str(tag.contents).splitlines():
            entry = line.split('|', 1)[0].strip()
            if entry:
                add_image('gallery_entry', line, title=file_title(entry))

    review_reason = None if boxes else (
        'taxonbar_without_taxobox' if has_taxonbar else 'regex_false_positive')
    article = dict(page_id=page['page_id'], revision_id=page['revision_id'],
                   title=page['title'], timestamp=page['timestamp'],
                   source_file=page['source_file'],
                   selection_signal=page['selection_signal'],
                   taxobox_count=len(boxes), has_taxonbar=has_taxonbar,
                   section_only=bool(boxes) and all(box['section_path'] for box in boxes),
                   location_unresolved_count=sum(box['start'] is None for box in boxes),
                   review_reason=review_reason)
    return article, boxes, parameters, images


def parse_shard(source, output, aliases, input_sha256=None, parser_sha256=None):
    source, output = Path(source), Path(output)
    output.mkdir(parents=True, exist_ok=True)
    if any((output / name).exists() for name in (*ARTIFACTS, 'report.json')):
        raise FileExistsError('Choose a fresh output directory')
    started = time.monotonic()
    buffers = {kind: [] for kind in SCHEMAS}
    writers = {}
    counts = Counter()

    def flush(kind):
        if not buffers[kind]:
            return
        schema = SCHEMAS[kind]
        if kind not in writers:
            writers[kind] = pq.ParquetWriter(
                output / (kind + '.parquet.partial'), schema, compression='snappy',
                use_dictionary=[field for field in ('title', 'source_file', 'selection_signal',
                                                    'family', 'template_name', 'raw_name',
                                                    'normalized_name', 'source_kind', 'file_title')
                                if field in schema.names])
        writers[kind].write_table(pa.Table.from_pylist(buffers[kind], schema=schema),
                                  row_group_size=1024)
        buffers[kind].clear()

    try:
        reader = pq.ParquetFile(source)
        for batch in reader.iter_batches(batch_size=128):
            for page in batch.to_pylist():
                article, boxes, params, images = parse_page(page, aliases)
                for kind, rows in (('articles', [article]), ('taxoboxes', boxes),
                                   ('taxobox_parameters', params),
                                   ('image_references', images)):
                    buffers[kind].extend(rows)
                    if len(buffers[kind]) >= 4096:
                        flush(kind)
                counts['articles'] += 1
                counts['taxoboxes'] += len(boxes)
                counts['taxobox_parameters'] += len(params)
                counts['image_references'] += len(images)
                counts[article['review_reason'] or 'confirmed_articles'] += 1
                counts['location_unresolved'] += article['location_unresolved_count']
                counts['section_only_articles'] += article['section_only']
                if counts['articles'] % 5000 == 0:
                    print(json.dumps(dict(phase='parse', source=source.parent.name,
                                          articles=counts['articles'],
                                          taxoboxes=counts['taxoboxes'],
                                          seconds=round(time.monotonic() - started, 1))), flush=True)
        for kind in SCHEMAS:
            flush(kind)
    finally:
        for writer in writers.values():
            writer.close()
    for kind, schema in SCHEMAS.items():
        path = output / (kind + '.parquet.partial')
        if kind not in writers:
            pq.write_table(pa.Table.from_pylist([], schema=schema), path, compression='snappy')
        (output / (kind + '.parquet.partial')).rename(output / (kind + '.parquet'))
    report = dict(schema_version=1, source=str(source), input_sha256=input_sha256,
                  parser_sha256=parser_sha256, **counts,
                  elapsed_seconds=round(time.monotonic() - started, 2),
                  output_bytes={name: (output / name).stat().st_size for name in ARTIFACTS},
                  limitations=[
                      'Template expansion and displayed taxonomy are not resolved.',
                      'Image titles are provisional references, not verified file identities or downloads.',
                      'The input candidate pool is not an exhaustive set of lifeform-related pages.',
                  ])
    (output / 'report.json.partial').write_text(json.dumps(report, indent=2) + '\n')
    (output / 'report.json.partial').rename(output / 'report.json')
    print(json.dumps(dict(phase='complete', source=source.parent.name,
                          articles=counts['articles'], taxoboxes=counts['taxoboxes'],
                          seconds=report['elapsed_seconds'])), flush=True)
    return report


def run(shard=None, all_shards=False):
    run_info = json.loads((SOURCE_ROOT / '_RUN.json').read_text())
    complete = json.loads((SOURCE_ROOT / '_COMPLETE.json').read_text())
    if complete.get('fingerprint') != run_info.get('fingerprint') or complete.get('phase') != 'complete':
        raise ValueError('Input pool is incomplete or mismatched')
    files = run_info['files']
    if shard and all_shards:
        raise ValueError('Choose one shard or --all')
    if not all_shards:
        files = [item for item in files if item['name'].removesuffix('.xml.bz2') == shard]
        if len(files) != 1:
            raise ValueError('Unknown shard: ' + str(shard))
    parser_sha256 = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    for item in files:
        stem = item['name'].removesuffix('.xml.bz2')
        input_dir, output_dir = SOURCE_ROOT / stem, OUTPUT_ROOT / stem
        marker = json.loads((input_dir / '_COMPLETE.json').read_text())
        descriptor = marker['artifacts']['pool.parquet']
        source = input_dir / 'pool.parquet'
        if source.stat().st_size != descriptor['bytes']:
            raise ValueError('Input size differs from marker: ' + stem)
        report_path = output_dir / 'report.json'
        if report_path.exists():
            report = json.loads(report_path.read_text())
            if (report.get('input_sha256') != descriptor['sha256'] or
                    report.get('parser_sha256') != parser_sha256 or
                    any(not (output_dir / name).exists() or
                        (output_dir / name).stat().st_size != report['output_bytes'][name]
                        for name in ARTIFACTS)):
                raise ValueError('Existing output differs from parser or input: ' + stem)
            print(json.dumps(dict(phase='skip', shard=stem, articles=report['articles'])), flush=True)
            continue
        if output_dir.exists() and any(output_dir.iterdir()):
            raise ValueError('Incomplete output directory requires inspection: ' + str(output_dir))
        parse_shard(source, output_dir, run_info['aliases'], descriptor['sha256'], parser_sha256)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--shard', help='One source stem, e.g. enwiki-2026-10-01-p10p1113635')
    group.add_argument('--all', action='store_true', help='Process all completed pool shards')
    args = parser.parse_args()
    run(args.shard, args.all)
