# First-pass lifeform extraction

This exploratory extractor selects English Wikipedia main-namespace articles
that invoke a taxobox-family template. It preserves original wikitext rather
than trying to render article text or expand the taxonomic classification.

## Run

```sh
uv venv .venv
uv pip install --python .venv/bin/python -r requirements.txt
.venv/bin/python -m unittest -v test_extract_lifeforms.py test_copy_dump_set.py
.venv/bin/python -u extract_lifeforms.py \
  data/enwiki-2026-10-01-p10p1113635.xml.bz2 \
  --output results/enwiki-2026-10-01-p10p1113635
```

Choose a fresh output directory for each run. The tool refuses to overwrite
published results. Progress is emitted as JSON lines to standard output.

## Outputs

- `candidates.ndjson.gz`: one selected page per line, containing page ID, title,
  namespace, revision ID, timestamp, source filename, redirect flag, original
  wikitext, and canonical `matched_templates` selection reasons.
- `dependencies.ndjson.gz`: taxonomy template pages, recognized taxobox templates
  and aliases, and Autotaxobox module pages encountered in this file. These are
  collected for investigation; this is not a complete rendering dependency closure.
- `report.json`: source identity/size, scanned/selected counts, aliases, candidate
  counts per taxobox family, examples, template invocation frequencies within
  candidates, compressed output sizes, timing, and coverage limitations.

The first pass indexes template redirects, resolving chains and avoiding cycles.
The second pass uses a broad template-name prefilter followed by mwparserfromhell
to confirm template invocations, excluding comments and nowiki text. Article
redirects and non-article namespaces are excluded from candidates. XML elements
are released after each page. Files are processed while compressed; the whole
XML document is never materialized on disk or in memory.

Output is written under `.partial` filenames and renamed on successful completion.
`report.json` is published last and is the completed-run marker. Renaming the
three files is not a single atomic operation; readers must require the report.

## Limits and next steps

Aliases and taxonomy dependencies can reside in other dump files. Counts from
this page-ID range are not a random sample of Wikipedia. Taxobox detection does
not establish complete coverage of all lifeform-related subjects. Template
frequency statistics describe selected candidates, not every Wikipedia page.
No image binaries are downloaded and no complete taxonomy is resolved yet.

Next, inspect examples and field distributions from this smaller candidate set.
Use DuckDB as a derived analysis database if useful; keep compressed NDJSON as
the reusable source artifact. Detailed parameter extraction and Parquet output
can follow once the markup has been inspected.

## Candidate inspection

```sh
.venv/bin/python -u inspect_candidates.py \
  results/enwiki-2026-10-01-p10p1113635/candidates.ndjson.gz \
  --output results/enwiki-2026-10-01-p10p1113635/inspection.json
```

This exploratory profile counts parameter occurrences and media signals and
retains examples of complex values and ambiguous subjects. Findings and their
limitations are summarized in [INSPECTION.md](INSPECTION.md).

The extended single-file spike (selection recall, parser behavior, identity
components, section context, scalar fields, and text fidelity) is documented in
[SPIKE_FINDINGS.md](SPIKE_FINDINGS.md). These are investigative artifacts;
the original extraction outputs have not been replaced or expanded.

## Complete-export worker

The VM worker extends the first-pass extractor to the complete Azure import,
with cross-shard aliases, parser fixes, a separate review stream, source checksum
verification, persisted checkpoints, and per-shard uploads. Deployment, output
layout, and status/recovery commands are in [EXTRACTION_RUN.md](EXTRACTION_RUN.md).
The single-file spike artifacts above remain separate from this versioned run.
