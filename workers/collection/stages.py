"""Target-scoped adapters around the investigative transforms."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys

from .manifest import sha256_file


def source_set(inputs, scratch, store):
    (scratch / 'pins.json').write_text(json.dumps(inputs['pins'], sort_keys=True))
    return ['pins.json']


def _image_references(inputs, scratch, store):
    import pyarrow.parquet as pq
    from .image_sql import canonical_key
    candidates = set()
    for index, uri in enumerate(inputs['image_references']):
        path = store.download(uri, scratch / '_input' / f'images-{index}.parquet')
        for batch in pq.ParquetFile(path).iter_batches(columns=['file_title'], batch_size=20000):
            candidates.update(canonical_key(title) for title in batch.column(0).to_pylist() if title)
    return candidates


def enwiki_image_metadata(inputs, scratch, store):
    from .image_sql import convert_image_sql
    report = convert_image_sql(store.iter_chunks(inputs['dump']), scratch / 'images.parquet',
                               'enwiki', inputs['source_dump_sha1'])
    (scratch / 'report.json').write_text(json.dumps(report, sort_keys=True))
    return ['images.parquet', 'report.json']


def commons_image_metadata(inputs, scratch, store):
    from .image_sql import convert_image_sql
    candidates = _image_references(inputs, scratch, store)
    report = convert_image_sql(store.iter_chunks(inputs['dump']), scratch / 'images.parquet',
                               'commonswiki', inputs['source_dump_sha1'], candidates)
    (scratch / 'report.json').write_text(json.dumps(report, sort_keys=True))
    return ['images.parquet', 'report.json']


def resolved_image_metadata(inputs, scratch, store):
    import duckdb
    import pyarrow as pa
    import pyarrow.parquet as pq
    en = store.download(inputs['enwiki_metadata'], scratch / '_input/enwiki.parquet')
    commons = store.download(inputs['commons_metadata'], scratch / '_input/commons.parquet')
    candidates = _image_references(inputs, scratch, store)
    keys = scratch / '_input/candidates.parquet'
    pq.write_table(pa.table({'canonical_key': sorted(candidates)}), keys, compression='snappy')
    db = duckdb.connect()
    try:
        db.execute('''CREATE TEMP TABLE resolved AS
            SELECT c.canonical_key,
                   COALESCE(e.file_title, m.file_title) AS file_title,
                   COALESCE(e.source_wiki, m.source_wiki) AS source_wiki,
                   CASE WHEN e.match_count > 1 OR (e.file_title IS NULL AND m.match_count > 1)
                        THEN NULL ELSE COALESCE(e.original_bytes, m.original_bytes) END AS original_bytes,
                   COALESCE(e.width, m.width) AS width,
                   COALESCE(e.height, m.height) AS height,
                   COALESCE(e.media_type, m.media_type) AS media_type,
                   COALESCE(e.mime, m.mime) AS mime,
                   COALESCE(e.uploaded_at, m.uploaded_at) AS uploaded_at,
                   COALESCE(e.sha1_base36, m.sha1_base36) AS sha1_base36,
                   COALESCE(e.source_dump_sha1, m.source_dump_sha1) AS source_dump_sha1,
                   CASE WHEN e.match_count > 1 OR (e.file_title IS NULL AND m.match_count > 1)
                             THEN 'ambiguous'
                        WHEN e.file_title IS NULL AND m.file_title IS NULL THEN 'missing'
                        WHEN COALESCE(e.media_type, m.media_type, '') NOT IN ('BITMAP', 'DRAWING')
                             THEN 'unsupported-media'
                        WHEN e.file_title IS NOT NULL THEN 'enwiki-local'
                        WHEN m.file_title IS NOT NULL THEN 'commons'
                        ELSE 'missing' END AS status
            FROM read_parquet(?) c
            LEFT JOIN (
                SELECT *, count(*) OVER (PARTITION BY canonical_key) AS match_count,
                       row_number() OVER (PARTITION BY canonical_key ORDER BY file_title) AS match_number
                FROM read_parquet(?)
            ) e ON c.canonical_key=e.canonical_key AND e.match_number=1
            LEFT JOIN (
                SELECT *, count(*) OVER (PARTITION BY canonical_key) AS match_count,
                       row_number() OVER (PARTITION BY canonical_key ORDER BY file_title) AS match_number
                FROM read_parquet(?)
            ) m ON c.canonical_key=m.canonical_key AND m.match_number=1''', [str(keys), str(en), str(commons)])
        output = str(scratch / 'resolved_images.parquet').replace("'", "''")
        db.execute(f"COPY resolved TO '{output}' (FORMAT PARQUET, COMPRESSION SNAPPY)")
        report = dict(db.execute('SELECT status, count(*) FROM resolved GROUP BY status').fetchall())
    finally:
        db.close()
    (scratch / 'report.json').write_text(json.dumps({'candidates': len(candidates),
                                                    'by_status': report}, sort_keys=True))
    return ['resolved_images.parquet', 'report.json']


def alias_index(inputs, scratch, store):
    from extract_lifeforms import index_redirects
    source = inputs['dump']
    redirects = index_redirects(source['url'], source['sha256'])
    (scratch / 'index.json').write_text(json.dumps({
        'source_sha256': source['sha256'], 'redirects': redirects}, sort_keys=True))
    return ['index.json']


def alias_seed(inputs, scratch, store):
    from extract_lifeforms import resolve_aliases
    redirects = {}
    for uri in inputs['indexes']:
        redirects.update(store.read_json(uri)['redirects'])
    aliases = resolve_aliases(redirects)
    (scratch / 'aliases.json').write_text(json.dumps({'aliases': aliases}, sort_keys=True))
    return ['aliases.json']


def candidate_pool_shard(inputs, scratch, store):
    from build_candidate_pool import build_pool
    source = inputs['dump']
    aliases = store.read_json(inputs['seed'])['aliases']
    build_pool(source['url'], scratch, aliases, source['sha256'], source['bytes'])
    return ['pool.parquet', 'dependencies.parquet', 'redirects.json', 'report.json']


def candidate_pool_finalize(inputs, scratch, store):
    from build_candidate_pool import check_aliases
    from extract_lifeforms import resolve_aliases
    redirects = {}
    for uri in inputs['pool_redirects']:
        redirects.update(store.read_json(uri))
    discovered = resolve_aliases(redirects)
    check_aliases(store.read_json(inputs['seed'])['aliases'], discovered)
    (scratch / 'aliases-discovered.json').write_text(json.dumps({'aliases': discovered}, sort_keys=True))
    return ['aliases-discovered.json']


def parse_shard(inputs, scratch, store):
    from parse_candidate_pool import parse_shard as parse
    pool = store.download(inputs['pool']['url'], scratch / '_input/pool.parquet')
    aliases = store.read_json(inputs['seed'])['aliases']
    parser_hash = sha256_file(Path(__file__).resolve().parents[2] / 'parse_candidate_pool.py')
    parse(pool, scratch, aliases, inputs['pool']['sha256'], parser_hash)
    return ['articles.parquet', 'taxoboxes.parquet',
            'taxobox_parameters.parquet', 'image_references.parquet', 'report.json']


def parse_finalize(inputs, scratch, store):
    return []


def ott_names(inputs, scratch, store):
    from transform_ott_taxonomy import transform
    source = store.download(inputs['taxonomy'], scratch / '_input/taxonomy.tsv')
    transform(source, scratch / 'taxonomy_names.parquet', scratch / 'report.json')
    return ['taxonomy_names.parquet', 'report.json']


def ott_correlation(inputs, scratch, store):
    for shard in inputs['parse_shards']:
        directory = scratch / '_input/parse' / shard['key']
        for name in ('articles.parquet', 'taxoboxes.parquet', 'taxobox_parameters.parquet'):
            store.download(shard[name], directory / name)
    ott = store.download(inputs['ott_names'], scratch / '_input/ott/taxonomy_names.parquet')
    output = scratch / '_output'
    subprocess.run([sys.executable, 'correlate_ott_wikipedia.py',
                    '--parse-root', str(scratch / '_input/parse'), '--ott', str(ott),
                    '--output', str(output)], check=True)
    names = ['wiki_names.parquet', 'exact_matches.parquet', 'match_status.parquet',
             'near_name_candidates.parquet', 'report.json']
    for name in names:
        (output / name).replace(scratch / name)
    return names


STAGES = {
    'alias-index': alias_index,
    'alias-seed': alias_seed,
    'candidate-pool-shard': candidate_pool_shard,
    'candidate-pool-finalize': candidate_pool_finalize,
    'parse-shard': parse_shard,
    'parse-finalize': parse_finalize,
    'ott-names': ott_names,
    'ott-correlation': ott_correlation,
    'source-set': source_set,
    'enwiki-image-metadata': enwiki_image_metadata,
    'commons-image-metadata': commons_image_metadata,
    'resolved-image-metadata': resolved_image_metadata,
}


def execute(kind, inputs, scratch, store, output_prefix, recipe_version, processor=None):
    scratch = Path(scratch)
    scratch.mkdir(parents=True, exist_ok=True)
    stage = processor or STAGES[kind]
    names = stage(inputs, scratch, store)
    if len(set(names)) != len(names):
        raise ValueError('Stage produced duplicate artifact names')
    artifacts = []
    for name in names:
        path = scratch / name
        if not path.is_file():
            raise RuntimeError(f'Stage did not produce {name}')
        descriptor = store.upload(path, output_prefix + name)
        artifacts.append({'Name': name, 'Path': output_prefix + name,
                          'Length': descriptor['Length'], 'Sha256': descriptor['Sha256'],
                          'Format': path.suffix.lstrip('.') or 'text'})
    target = inputs['Target']
    marker = {'Target': target, 'RecipeVersion': recipe_version,
              'Artifacts': artifacts,
              'InputFingerprints': inputs.get('InputFingerprints', {})}
    store.write_json(output_prefix + '_COMPLETE.json', marker)
    return marker
