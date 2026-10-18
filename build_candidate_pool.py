"""Stream one Wikimedia bzip2 shard into a broad, lossless Snappy Parquet pool."""
import json
from pathlib import Path
import re
import time

import pyarrow as pa
import pyarrow.parquet as pq

from extract_lifeforms import normalize, pages, redirect_target, source_name


SCHEMA = pa.schema([
    ('page_id', pa.int64()), ('title', pa.string()), ('namespace', pa.int32()),
    ('revision_id', pa.int64()), ('timestamp', pa.string()),
    ('source_file', pa.string()), ('wikitext', pa.large_string()),
    ('selection_signal', pa.string()),
])


def check_aliases(seed, discovered):
    if seed != discovered:
        missing = sorted(set(discovered) - set(seed))
        changed = sorted(name for name in seed.keys() & discovered.keys()
                         if seed[name] != discovered[name])
        removed = sorted(set(seed) - set(discovered))
        raise ValueError(f'Taxobox alias set changed; reprocess with a new seed: '
                         f'added={missing}, changed={changed}, removed={removed}')


def _pattern(aliases):
    alternatives = '|'.join(re.escape(name).replace(r'\ ', r'[\s_]+')
                            for name in sorted(aliases, key=len, reverse=True))
    return re.compile(r'\{\{\s*(?:(?:subst:|safesubst:)\s*)?(?:Template\s*:\s*)?('
                      + alternatives + r')\s*(?=[|}])', re.I)


def build_pool(source, output, aliases, expected_sha256=None, source_bytes=None):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    artifacts = ('pool.parquet', 'dependencies.parquet', 'redirects.json', 'report.json')
    if any((output / name).exists() for name in artifacts):
        raise FileExistsError('Choose a fresh output directory')
    started = time.monotonic()
    pattern = _pattern(aliases)
    taxonbar = re.compile(r'\{\{\s*(?:Template\s*:\s*)?Taxonbar\s*[|}]', re.I)
    redirects = {}
    counts = dict(pages_scanned=0, articles_scanned=0, pool_pages=0, dependencies=0,
                  taxobox_signals=0, taxonbar_signals=0, template_redirects=0)
    writers = {}
    buffers = {'pool': [], 'dependencies': []}

    def flush(kind):
        if not buffers[kind]:
            return
        path = output / (kind + '.parquet.partial')
        table = pa.Table.from_pylist(buffers[kind], schema=SCHEMA)
        if kind not in writers:
            writers[kind] = pq.ParquetWriter(path, SCHEMA, compression='snappy',
                                             use_dictionary=['title', 'source_file', 'selection_signal'],
                                             write_statistics=True)
        writers[kind].write_table(table, row_group_size=512)
        buffers[kind].clear()

    try:
        for page in pages(source, expected_sha256):
            counts['pages_scanned'] += 1
            namespace, title, wikitext = page['namespace'], page['title'], page['wikitext']
            if namespace == 10:
                target = redirect_target(wikitext)
                if target is not None:
                    redirects[normalize(title)] = target
                    counts['template_redirects'] += 1
            dependency = (namespace == 10 and (
                title.casefold().startswith('template:taxonomy/') or normalize(title) in aliases))
            dependency = dependency or (namespace == 828 and
                                        title.casefold().startswith('module:autotaxobox'))
            if dependency:
                buffers['dependencies'].append({**page, 'selection_signal': 'dependency'})
                counts['dependencies'] += 1
                if len(buffers['dependencies']) >= 512:
                    flush('dependencies')
            if namespace == 0 and not page['is_redirect'] and not re.match(
                    r'^\s*#redirect\b', wikitext, re.I):
                counts['articles_scanned'] += 1
                probe = re.sub(r'<!--.*?-->', '', wikitext, flags=re.S) if '<!--' in wikitext else wikitext
                has_taxobox = bool(pattern.search(probe))
                has_taxonbar = bool(taxonbar.search(probe)) if not has_taxobox else False
                if has_taxobox or has_taxonbar:
                    page['selection_signal'] = 'taxobox_name' if has_taxobox else 'taxonbar'
                    buffers['pool'].append(page)
                    counts['pool_pages'] += 1
                    counts['taxobox_signals' if has_taxobox else 'taxonbar_signals'] += 1
                    if len(buffers['pool']) >= 512:
                        flush('pool')
            if counts['pages_scanned'] % 25000 == 0:
                print(json.dumps(dict(phase='pool', source=source_name(source), **counts,
                                      seconds=round(time.monotonic() - started, 1))), flush=True)
        flush('pool')
        flush('dependencies')
    finally:
        for writer in writers.values():
            writer.close()
    # ParquetWriter does not create a file for an empty stream.
    for kind in buffers:
        if kind not in writers:
            pq.write_table(pa.Table.from_pylist([], schema=SCHEMA),
                           output / (kind + '.parquet.partial'), compression='snappy')
    (output / 'redirects.json.partial').write_text(json.dumps(redirects, sort_keys=True) + '\n')
    report = dict(schema_version=1, source_file=source_name(source),
                  source_sha256=expected_sha256, source_bytes=source_bytes,
                  seed_aliases=aliases, **counts,
                  elapsed_seconds=round(time.monotonic() - started, 2),
                  note='Broad lexical pool; later parsing must reject comments, nowiki, and unrelated uses.')
    (output / 'report.json.partial').write_text(json.dumps(report, indent=2) + '\n')
    for name in artifacts:
        (output / (name + '.partial')).rename(output / name)
    return report
