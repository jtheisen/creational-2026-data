"""Inventory and publish existing local outputs under immutable Azure prefixes.

Run --check locally. Run --apply on the VM after transferring the local inputs;
the VM managed identity supplies Blob credentials through the existing helper.
"""

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path


ACCOUNT = 'https://creational.blob.core.windows.net'
COLLECTION = 'enwiki/2026-10-01'
PARSE_FILES = ('articles.parquet', 'taxoboxes.parquet',
               'taxobox_parameters.parquet', 'image_references.parquet', 'report.json')
CORRELATION_FILES = ('wiki_names.parquet', 'exact_matches.parquet',
                     'match_status.parquet', 'near_name_candidates.parquet', 'report.json')


@dataclass(frozen=True)
class Group:
    kind: str
    key: str
    recipe: str
    local: Path
    destination: str
    members: tuple[str, ...]


def _check_members(directory: Path, names: tuple[str, ...], expected: dict[str, int]) -> None:
    for name in names:
        path = directory / name
        if not path.is_file():
            raise ValueError(f'missing {path}')
        if name in expected and path.stat().st_size != expected[name]:
            raise ValueError(f'length mismatch for {path}: {path.stat().st_size} != {expected[name]}')


def inventory(root: Path) -> list[Group]:
    root = Path(root)
    result = []
    for directory in sorted((root / 'results/candidate-parse-v1').glob('*')):
        if not directory.is_dir():
            continue
        report_path = directory / 'report.json'
        if not report_path.is_file():
            raise ValueError(f'missing {report_path}')
        report = json.loads(report_path.read_text())
        _check_members(directory, PARSE_FILES, report['output_bytes'])
        stem = directory.name
        key = stem.removeprefix('enwiki-2026-10-01-')
        result.append(Group('parse-shard', key, 'candidate-parse-v1', directory,
            f'{ACCOUNT}/wikimedia-extracts/{COLLECTION}/candidate-parse-v1/{stem}/', PARSE_FILES))

    ott_source = root / 'data/opentree/ott3.3/taxonomy.tsv'
    ott_dir = root / 'results/opentree-ott3.3'
    if ott_source.exists() or ott_dir.exists():
        if not (ott_dir / 'report.json').is_file():
            raise ValueError('missing OTT report.json')
        report = json.loads((ott_dir / 'report.json').read_text())
        _check_members(ott_source.parent, ('taxonomy.tsv',), {'taxonomy.tsv': report['source_bytes']})
        _check_members(ott_dir, ('taxonomy_names.parquet', 'report.json'),
                       {'taxonomy_names.parquet': report['output_bytes']})
        result.append(Group('ott-source', 'all', 'ott3.3', ott_source.parent,
            f'{ACCOUNT}/wikimedia-imports/opentree/ott3.3/', ('taxonomy.tsv',)))
        result.append(Group('ott-names', 'all', 'ott3.3', ott_dir,
            f'{ACCOUNT}/wikimedia-extracts/{COLLECTION}/ott3.3/',
            ('taxonomy_names.parquet', 'report.json')))

    correlation = root / 'results/ott-wikipedia-correlation-v3'
    if correlation.exists():
        report = json.loads((correlation / 'report.json').read_text())
        expected = {name: descriptor['bytes'] for name, descriptor in report['files'].items()}
        _check_members(correlation, CORRELATION_FILES, expected)
        result.append(Group('ott-correlation', 'all', 'ott-wikipedia-correlation-v3', correlation,
            f'{ACCOUNT}/wikimedia-extracts/{COLLECTION}/ott-wikipedia-correlation-v3/',
            CORRELATION_FILES))
    return result


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def manifest(group: Group) -> dict:
    return {
        'Target': {'Collection': COLLECTION, 'Kind': group.kind, 'Key': group.key},
        'RecipeVersion': group.recipe,
        'Artifacts': [
            {'Name': name, 'Path': group.destination + name,
             'Length': (group.local / name).stat().st_size,
             'Sha256': _hash(group.local / name),
             'Format': Path(name).suffix.lstrip('.') or 'text'}
            for name in group.members
        ],
        'InputFingerprints': {},
    }


def publish(group: Group) -> str:
    from extract_blob_set import blob_matches, read_json, upload, write_json

    marker_url = group.destination + '_COMPLETE.json'
    descriptor = manifest(group)
    existing = read_json(marker_url)
    if existing is not None:
        if existing != descriptor:
            raise ValueError(f'existing marker differs: {marker_url}')
        if not all(blob_matches(item['Path'], {'bytes': item['Length'], 'sha256': item['Sha256']})
                   for item in descriptor['Artifacts']):
            raise ValueError(f'existing group is incomplete: {marker_url}')
        return 'already published'
    for item in descriptor['Artifacts']:
        if not blob_matches(item['Path'], {'bytes': item['Length'], 'sha256': item['Sha256']}):
            upload(group.local / item['Name'], item['Path'])
    write_json(marker_url, descriptor)
    return 'published'


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path('.'))
    action = parser.add_mutually_exclusive_group()
    action.add_argument('--check', action='store_true')
    action.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    groups = inventory(args.root)
    if len([g for g in groups if g.kind == 'parse-shard']) != 19 or len(groups) != 22:
        raise ValueError(f'Expected 19 parse groups plus OTT source/names/correlation, got {len(groups)}')
    for group in groups:
        state = publish(group) if args.apply else 'ready'
        print(json.dumps({'target': f'{group.kind}/{group.key}', 'state': state,
                          'bytes': sum((group.local / name).stat().st_size for name in group.members)}), flush=True)


if __name__ == '__main__':
    main()
