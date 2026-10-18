"""Investigative exact correlation of parsed Wikipedia taxoboxes with OTT 3.3."""

import argparse
from collections import Counter
import html
import json
from pathlib import Path
import re

import duckdb
import mwparserfromhell as mw
import pyarrow as pa
import pyarrow.parquet as pq

from transform_ott_taxonomy import classify_name


PARSE_ROOT = Path('results/candidate-parse-v1')
OTT = Path('results/opentree-ott3.3/taxonomy_names.parquet')
OUTPUT = Path('results/ott-wikipedia-correlation-v3')
IDENTITY_KEYS = ('taxon', 'genus', 'species', 'subspecies', 'varietas', 'variety',
                 'binomial', 'trinomial')
SKIP_FAMILIES = {'Virusbox', 'Hybridbox', 'Paraphyletic group',
                 'Population taxobox', 'Ichnobox', 'Oobox'}
SCHEMA = pa.schema([
    ('page_id', pa.int64()), ('revision_id', pa.int64()),
    ('box_index', pa.int32()), ('title', pa.string()), ('family', pa.string()),
    ('proposed_name', pa.string()), ('name_key', pa.string()),
    ('name_form', pa.string()), ('method', pa.string()),
    ('review_reason', pa.string()),
    ('used_parameter_indices', pa.list_(pa.int32())),
    ('has_duplicate_identity_keys', pa.bool_()),
])


def sql_string(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def clean_identity(raw: str) -> str | None:
    """Strip simple wiki display markup; reject values requiring expansion."""
    code = mw.parse(raw)
    for comment in list(code.filter_comments()):
        code.remove(comment)
    for tag in list(code.filter_tags()):
        if str(tag.tag).casefold() == 'ref':
            code.remove(tag)
    if code.filter_templates() or code.filter_arguments():
        return None
    return re.sub(r'\s+', ' ', html.unescape(code.strip_code())).strip()


def derive_name(family: str, title: str,
                parameters: list[tuple[int, str, str]]) -> dict:
    if family in SKIP_FAMILIES:
        return dict(proposed_name=None, name_key=None, name_form=None,
                    method='none', review_reason='excluded_family',
                    used_parameter_indices=[], has_duplicate_identity_keys=False)
    values = {}
    duplicate = False
    for index, key, raw in parameters:
        if key not in IDENTITY_KEYS:
            continue
        if key in values:
            duplicate = True
        values[key] = (index, clean_identity(raw))

    chosen = []
    method = 'none'
    if values.get('taxon') and values['taxon'][1]:
        chosen, method = ['taxon'], 'explicit_taxon'
    elif family in ('Subspeciesbox', 'Infraspeciesbox', 'Infraspeciesbox special'):
        if all(values.get(key) and values[key][1] for key in ('genus', 'species', 'subspecies')):
            chosen, method = ['genus', 'species', 'subspecies'], 'genus_species_subspecies'
        elif all(values.get(key) and values[key][1] for key in ('genus', 'species', 'varietas')):
            chosen, method = ['genus', 'species', 'varietas'], 'genus_species_variety'
        elif all(values.get(key) and values[key][1] for key in ('genus', 'species', 'variety')):
            chosen, method = ['genus', 'species', 'variety'], 'genus_species_variety'
    if not chosen and all(values.get(key) and values[key][1] for key in ('genus', 'species')):
        chosen, method = ['genus', 'species'], 'genus_species'
    if not chosen and values.get('trinomial') and values['trinomial'][1]:
        chosen, method = ['trinomial'], 'legacy_trinomial'
    if not chosen and values.get('binomial') and values['binomial'][1]:
        chosen, method = ['binomial'], 'legacy_binomial'

    used = [values[key][0] for key in chosen]
    if chosen == ['genus', 'species', 'subspecies']:
        marker = ' subsp. ' if family.startswith('Infraspeciesbox') else ' '
        name = values['genus'][1] + ' ' + values['species'][1] + marker + values['subspecies'][1]
    elif chosen == ['genus', 'species', 'varietas'] or chosen == ['genus', 'species', 'variety']:
        name = values['genus'][1] + ' ' + values['species'][1] + ' var. ' + values[chosen[-1]][1]
    elif chosen == ['genus', 'species']:
        name = values['genus'][1] + ' ' + values['species'][1]
    elif chosen:
        name = values[chosen[0]][1]
    elif family == 'Automatic taxobox' and not values.get('taxon'):
        name, method = title, 'title_fallback'
    else:
        name = None

    if name is None:
        reason = ('unresolved_markup' if any(value is None for _, value in values.values())
                  else 'no_identity_fields')
        return dict(proposed_name=None, name_key=None, name_form=None,
                    method=method, review_reason=reason,
                    used_parameter_indices=used,
                    has_duplicate_identity_keys=duplicate)
    form, key = classify_name(name, '')
    return dict(proposed_name=name, name_key=key,
                name_form=form if key else None, method=method,
                review_reason=None if key else form,
                used_parameter_indices=used,
                has_duplicate_identity_keys=duplicate)


def extract_wiki_names(boxes: Path, parameters: Path, articles: Path,
                       output: Path, batch_size: int = 10_000) -> None:
    if batch_size <= 0:
        raise ValueError('batch_size must be positive')
    output.parent.mkdir(parents=True, exist_ok=True)
    con = duckdb.connect()
    keys = ','.join(sql_string(key) for key in IDENTITY_KEYS)
    query = f"""
        SELECT b.page_id,b.revision_id,b.box_index,a.title,b.family,
               p.parameter_index,p.normalized_name,p.raw_value
        FROM read_parquet({sql_string(boxes)}) b
        JOIN read_parquet({sql_string(articles)}) a
          ON b.page_id=a.page_id AND b.revision_id=a.revision_id
        LEFT JOIN read_parquet({sql_string(parameters)}) p
          ON b.page_id=p.page_id AND b.revision_id=p.revision_id
         AND b.box_index=p.box_index AND p.normalized_name IN ({keys})
        ORDER BY b.page_id,b.revision_id,b.box_index,p.parameter_index
    """
    cursor = con.execute(query)
    writer = pq.ParquetWriter(output, SCHEMA, compression='snappy')
    buffer = []
    current = None
    selected = []

    def flush():
        if current is None:
            return
        page_id, revision_id, box_index, title, family = current
        row = dict(page_id=page_id, revision_id=revision_id,
                   box_index=box_index, title=title, family=family,
                   **derive_name(family, title, selected))
        buffer.append(row)
        if len(buffer) >= batch_size:
            writer.write_table(pa.Table.from_pylist(buffer, schema=SCHEMA))
            buffer.clear()

    try:
        while rows := cursor.fetchmany(10_000):
            for page_id, revision_id, box_index, title, family, index, key, raw in rows:
                identity = (page_id, revision_id, box_index, title, family)
                if identity != current:
                    flush()
                    current = identity
                    selected = []
                if index is not None:
                    selected.append((index, key, raw))
        flush()
        if buffer:
            writer.write_table(pa.Table.from_pylist(buffer, schema=SCHEMA))
    finally:
        writer.close()
        con.close()


def match_exact(names: Path, ott: Path, exact: Path, status: Path) -> None:
    exact.parent.mkdir(parents=True, exist_ok=True)
    status.parent.mkdir(parents=True, exist_ok=True)
    con = duckdb.connect()
    con.execute(f"""
        COPY (
            SELECT w.page_id,w.revision_id,w.box_index,w.title,w.family,
                   w.proposed_name,w.method,w.name_key,
                   o.ott_id,o.parent_ott_id AS ott_parent_id,
                   o.name AS ott_name,o.rank AS ott_rank
            FROM read_parquet({sql_string(names)}) w
            JOIN read_parquet({sql_string(ott)}) o USING (name_key)
        ) TO {sql_string(exact)} (FORMAT PARQUET, COMPRESSION SNAPPY)
    """)
    con.execute(f"""
        COPY (
            SELECT w.*,
                   COALESCE(m.ott_match_count,0) AS ott_match_count,
                   CASE WHEN w.name_key IS NULL THEN 'no_name'
                        WHEN m.ott_match_count IS NULL THEN 'no_exact'
                        WHEN m.ott_match_count=1 THEN 'exact_unique'
                        ELSE 'exact_ambiguous' END AS match_status
            FROM read_parquet({sql_string(names)}) w
            LEFT JOIN (
                SELECT page_id,revision_id,box_index,
                       count(*) AS ott_match_count
                FROM read_parquet({sql_string(exact)})
                GROUP BY page_id,revision_id,box_index
            ) m USING (page_id,revision_id,box_index)
        ) TO {sql_string(status)} (FORMAT PARQUET, COMPRESSION SNAPPY)
    """)
    con.close()


def suggest_near_names(status: Path, ott: Path, output: Path) -> None:
    """Find edit-distance candidates within an exact genus or epithet block."""
    output.parent.mkdir(parents=True, exist_ok=True)
    con = duckdb.connect()
    con.execute(f"""
        COPY (
            WITH w AS (
                SELECT page_id,revision_id,box_index,title,family,proposed_name,
                       name_key,split_part(name_key,' ',1) AS genus,
                       split_part(name_key,' ',2) AS epithet
                FROM read_parquet({sql_string(status)})
                WHERE match_status='no_exact' AND name_form='binomial'
            ), o AS (
                SELECT ott_id,name AS ott_name,rank AS ott_rank,name_key,
                       split_part(name_key,' ',1) AS genus,
                       split_part(name_key,' ',2) AS epithet
                FROM read_parquet({sql_string(ott)})
                WHERE name_form='binomial'
            )
            SELECT w.page_id,w.revision_id,w.box_index,w.title,w.family,
                   w.proposed_name,o.ott_id,o.ott_name,o.ott_rank,
                   'epithet' AS differing_component,
                   levenshtein(w.epithet,o.epithet) AS edit_distance
            FROM w JOIN o USING (genus)
            WHERE abs(length(w.epithet)-length(o.epithet))<=2
              AND levenshtein(w.epithet,o.epithet) BETWEEN 1 AND 2
            UNION ALL
            SELECT w.page_id,w.revision_id,w.box_index,w.title,w.family,
                   w.proposed_name,o.ott_id,o.ott_name,o.ott_rank,
                   'genus' AS differing_component,
                   levenshtein(w.genus,o.genus) AS edit_distance
            FROM w JOIN o USING (epithet)
            WHERE abs(length(w.genus)-length(o.genus))<=2
              AND levenshtein(w.genus,o.genus) BETWEEN 1 AND 2
        ) TO {sql_string(output)} (FORMAT PARQUET, COMPRESSION SNAPPY)
    """)
    con.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--parse-root', type=Path, default=PARSE_ROOT)
    parser.add_argument('--ott', type=Path, default=OTT)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    names = output / 'wiki_names.parquet'
    exact = output / 'exact_matches.parquet'
    status = output / 'match_status.parquet'
    near = output / 'near_name_candidates.parquet'
    report = output / 'report.json'
    if any(path.exists() for path in (names, exact, status, near, report)):
        raise FileExistsError('Choose a fresh output directory')
    root = args.parse_root
    extract_wiki_names(root / '*/taxoboxes.parquet',
                       root / '*/taxobox_parameters.parquet',
                       root / '*/articles.parquet', names)
    match_exact(names, args.ott, exact, status)
    suggest_near_names(status, args.ott, near)
    con = duckdb.connect()
    counts = dict(con.sql(f"""
        SELECT match_status,count(*) FROM read_parquet({sql_string(status)})
        GROUP BY 1
    """).fetchall())
    methods = dict(con.sql(f"""
        SELECT method,count(*) FROM read_parquet({sql_string(names)}) GROUP BY 1
    """).fetchall())
    reasons = dict(con.sql(f"""
        SELECT review_reason,count(*) FROM read_parquet({sql_string(names)})
        WHERE review_reason IS NOT NULL GROUP BY 1
    """).fetchall())
    near_summary = dict(con.sql(f"""
        SELECT differing_component,count(*)
        FROM read_parquet({sql_string(near)}) GROUP BY 1
    """).fetchall())
    near_boxes = con.sql(f"""
        SELECT count(*) FROM (
            SELECT DISTINCT page_id,revision_id,box_index
            FROM read_parquet({sql_string(near)})
        )
    """).fetchone()[0]
    report.write_text(json.dumps({
        'match_status': counts, 'methods': methods, 'review_reasons': reasons,
        'near_name_candidates_by_component': near_summary,
        'near_name_candidate_boxes': near_boxes,
        'files': {p.name: {'rows': pq.read_metadata(p).num_rows,
                           'bytes': p.stat().st_size}
                  for p in (names, exact, status, near)},
    }, indent=2) + '\n')
    print(json.dumps(counts, indent=2))


if __name__ == '__main__':
    main()
