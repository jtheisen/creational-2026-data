"""Convert OTT 3.3 taxonomy.tsv to a conservative Wikipedia-name candidate table."""

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import re

import pyarrow as pa
import pyarrow.parquet as pq


SOURCE = Path('data/opentree/ott3.3/taxonomy.tsv')
OUTPUT = Path('results/opentree-ott3.3/taxonomy_names.parquet')
REPORT = Path('results/opentree-ott3.3/report.json')
HEADER = ['uid', 'parent_uid', 'name', 'rank', 'sourceinfo', 'uniqname', 'flags', '']
WORD = r'[A-Z][a-z]+(?:-[a-z]+)?'
EPITHET = r'[a-z]+(?:-[a-z]+)?'
UNINOMIAL = re.compile(rf'{WORD}\Z')
BINOMIAL = re.compile(rf'{WORD} {EPITHET}\Z')
INFRASPECIFIC = re.compile(
    rf'{WORD} {EPITHET} (?:subsp\. |ssp\. |var\. |f\. )?{EPITHET}\Z')
PROVISIONAL = re.compile(r'\b(?:sp|spp|cf|aff)\.', re.IGNORECASE)
VIRUS_NAME = re.compile(r'\bvirus\b', re.IGNORECASE)
SCHEMA = pa.schema([
    ('ott_id', pa.int64()), ('parent_ott_id', pa.int64()),
    ('name', pa.string()), ('name_key', pa.string()), ('name_form', pa.string()),
    ('rank', pa.string()), ('sourceinfo', pa.string()),
    ('uniqname', pa.string()), ('flags', pa.string()),
])


def classify_name(name: str, flags: str) -> tuple[str, str | None]:
    """Return a Latin-shaped form and key, or an exclusion reason and None."""
    if not name:
        return 'empty_name', None
    if 'viral' in flags.split(','):
        return 'viral_flag', None
    if PROVISIONAL.search(name):
        return 'provisional_name', None
    if name.startswith('Candidatus '):
        return 'candidatus_name', None
    if VIRUS_NAME.search(name):
        return 'virus_name', None
    if any(char.isdigit() for char in name):
        return 'contains_digit', None
    for form, pattern in (('uninomial', UNINOMIAL), ('binomial', BINOMIAL),
                          ('infraspecific', INFRASPECIFIC)):
        if pattern.fullmatch(name):
            return form, name.casefold()
    return 'noncanonical_name', None


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def transform(source: Path, output: Path, report_path: Path,
              batch_size: int = 10_000) -> dict:
    if batch_size <= 0:
        raise ValueError('batch_size must be positive')
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(output.name + '.partial')
    report_temporary = report_path.with_name(report_path.name + '.partial')
    source_rows = kept_rows = 0
    exclusions = Counter()
    forms = Counter()
    ranks = Counter()
    examples = defaultdict(list)
    batch = []
    writer = None
    try:
        with source.open(encoding='utf-8', newline='') as stream:
            header = stream.readline().rstrip('\r\n').split('\t|\t')
            if header != HEADER:
                raise ValueError(f'unexpected OTT header: {header!r}')
            for line_number, line in enumerate(stream, 2):
                fields = [value.strip() for value in line.rstrip('\r\n').split('\t|\t')]
                if len(fields) != len(HEADER) or fields[-1]:
                    raise ValueError(f'bad OTT row at line {line_number}: {fields!r}')
                uid, parent, name, rank, sourceinfo, uniqname, flags, _ = fields
                try:
                    ott_id = int(uid)
                    parent_ott_id = int(parent) if parent else None
                except ValueError as exc:
                    raise ValueError(f'bad OTT ID at line {line_number}') from exc
                source_rows += 1
                form, key = classify_name(name, flags)
                if key is None:
                    exclusions[form] += 1
                    if len(examples[form]) < 5:
                        examples[form].append({'ott_id': ott_id, 'name': name,
                                               'rank': rank})
                    continue
                batch.append(dict(ott_id=ott_id, parent_ott_id=parent_ott_id,
                                  name=name, name_key=key, name_form=form,
                                  rank=rank, sourceinfo=sourceinfo,
                                  uniqname=uniqname, flags=flags))
                kept_rows += 1
                forms[form] += 1
                ranks[rank] += 1
                if len(batch) >= batch_size:
                    if writer is None:
                        writer = pq.ParquetWriter(temporary, SCHEMA,
                                                  compression='snappy')
                    writer.write_table(pa.Table.from_pylist(batch, schema=SCHEMA))
                    batch.clear()
        if writer is None:
            writer = pq.ParquetWriter(temporary, SCHEMA, compression='snappy')
        if batch:
            writer.write_table(pa.Table.from_pylist(batch, schema=SCHEMA))
        writer.close()
        writer = None
        temporary.replace(output)
        report = {
            'source': str(source), 'source_bytes': source.stat().st_size,
            'source_sha256': sha256_file(source),
            'source_rows': source_rows, 'kept_rows': kept_rows,
            'excluded_by_reason': dict(sorted(exclusions.items())),
            'kept_by_form': dict(sorted(forms.items())),
            'kept_by_rank': dict(sorted(ranks.items())),
            'excluded_examples': dict(sorted(examples.items())),
            'output': str(output), 'output_bytes': output.stat().st_size,
            'output_sha256': sha256_file(output),
            'schema': str(SCHEMA),
        }
        report_temporary.write_text(json.dumps(report, indent=2) + '\n')
        report_temporary.replace(report_path)
        return report
    finally:
        if writer is not None:
            writer.close()
        temporary.unlink(missing_ok=True)
        report_temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, default=SOURCE)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    parser.add_argument('--report', type=Path, default=REPORT)
    args = parser.parse_args()
    report = transform(args.input, args.output, args.report)
    print(json.dumps({key: report[key] for key in
                      ('source_rows', 'kept_rows', 'excluded_by_reason',
                       'output_bytes')}, indent=2))


if __name__ == '__main__':
    main()
