# Local candidate-pool parsing

`parse_candidate_pool.py` reads the downloaded October 2026 candidate pool from
`data/candidate-pool-v1/` and writes derived Snappy Parquet files under
`results/candidate-parse-v1/`, one directory per input shard. It uses
`mwparserfromhell` with style-tag parsing disabled to confirm taxobox template
invocations, including aliases from the pool's pinned seed. The original pool
remains the authoritative source for complete wikitext.

Run one shard or resume the entire set from the project root:

```sh
.venv/bin/python -u parse_candidate_pool.py --shard enwiki-2026-10-01-p10p1113635
.venv/bin/python -u parse_candidate_pool.py --all
```

Completed shards are skipped only when their parser hash, input checksum,
output presence, and output lengths match the report. A changed parser needs a
new output version, not an in-place overwrite. For local parallel execution,
run distinct `--shard` commands concurrently; each writes only its own output
directory. `report.json` is published last for each shard.

## Derived files

| File | Grain and content |
| --- | --- |
| `articles.parquet` | One row for every candidate page: title and provenance, confirmed taxobox count, Taxonbar signal, section-only flag, and review reason when no taxobox was confirmed. |
| `taxoboxes.parquet` | One row per confirmed invocation: page/revision key, box index, canonical family, original template markup, approximate source span, section path, and top-level flag. |
| `taxobox_parameters.parquet` | One row per parameter, preserving source order, duplicate names, raw names and values, and whether the key was explicit. |
| `image_references.parquet` | Raw references from taxobox image/map fields, explicit File/Image links, and gallery entries. `file_title` is populated only for simple values; it is provisional and needs later resolution. |

Join article to taxobox by `(page_id, revision_id)`, and parameters or
taxobox-derived image references by `(page_id, revision_id, box_index)`. The
derived tables omit the full page wikitext because it remains in the input
pool. DuckDB can query all shards with a path such as
`read_parquet('results/candidate-parse-v1/*/taxoboxes.parquet')`.

## October 2026 local results

All 19 downloaded shards were parsed. Output totals:

| Measure | Count |
| --- | ---: |
| Candidate article rows | 503,735 |
| Articles with a confirmed taxobox | 502,980 |
| Taxobox invocations | 503,010 |
| Raw taxobox parameters | 3,127,793 |
| Raw image references | 479,865 |
| Taxonbar pages without a recognized taxobox, retained for review | 755 |
| Articles with taxoboxes only inside sections | 47 |

The four Parquet output families occupy 235,140,557 bytes in total. Every
Parquet row count matched its shard report. There were no duplicate article or
box keys, orphan boxes/parameters/box images, unresolved box locations, or
article-to-box count mismatches in the full local validation.

The first shard found 7,702 taxoboxes: all 7,695 from the earlier investigative
spike plus its seven documented recoveries, with no lost boxes. This validates
the parser transition against that shard; it is not a biological accuracy or
coverage measurement.

No taxonomy-template expansion, scientific-name validation, rendered article
text extraction, Commons file resolution, or image download happens here.
Apparent file titles and source offsets are aids for the next investigation,
not final identities or MediaWiki-rendered positions. The candidate pool itself
can miss lifeform-related pages that have neither a recognized taxobox name nor
a Taxonbar signal.
