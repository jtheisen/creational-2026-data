"""Target-scoped adapters around the investigative transforms."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys

from .manifest import sha256_file


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
