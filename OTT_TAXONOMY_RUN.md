# Open Tree taxonomy: local name-candidate conversion

This is a local investigation for a later Wikipedia correlation step. The
source is [Open Tree Taxonomy 3.3 `taxonomy.tsv`](https://files.opentreeoflife.org/ott/ott3.3/ott3.3/taxonomy.tsv),
downloaded on 2026-10-17 to the ignored
`data/opentree/ott3.3/taxonomy.tsv`. The server reported a 2021-06-01 last
modified date. The file is 416,869,933 bytes, SHA-256
`0c798437d90dedd1511e0ba2470323b6ffad54f2919634fe1a132bcc71c7239f`.
It has 4,528,126 data rows and the source columns `uid`, `parent_uid`, `name`,
`rank`, `sourceinfo`, `uniqname`, and `flags`, delimited by tab-pipe-tab.

Run the local conversion with:

```sh
.venv/bin/python -u transform_ott_taxonomy.py
```

It streams the TSV in batches and writes Snappy Parquet at
`results/opentree-ott3.3/taxonomy_names.parquet`, plus an audit summary at
`results/opentree-ott3.3/report.json`. Both directories are ignored by Git.
The TSV is retained, so excluded names can be reconsidered without another
download. The report records source and output hashes, row counts, exclusion
counts, example exclusions, and kept ranks/forms.

The Parquet has one row per **retained OTT ID**. Its columns are `ott_id`,
`parent_ott_id`, `name`, `name_key`, `name_form`, `rank`, `sourceinfo`,
`uniqname`, and `flags`. `name_key` is a case-folded exact-match candidate;
`name_form` is `uninomial`, `binomial`, or `infraspecific`. `parent_ott_id`
can refer to an excluded row. The source's `uniqname` is retained as a
disambiguator, not substituted for `name`. Repeated names across different
OTT IDs remain separate rows.

The deliberately conservative filter admits ASCII Latin-shaped names:
capitalized single words, capitalized genus plus lowercase epithet, or a
trinomial with optional `subsp.`, `ssp.`, `var.`, or `f.` marker. It excludes
provisional `sp.`/`spp.`/`cf.`/`aff.` labels, `Candidatus` names, names
containing digits, names containing the word `virus`, and rows carrying the
`viral` flag. Remaining names outside those shapes are also excluded. Other
OTT flags, including `extinct` and `hidden`, are retained as source metadata
and do not exclude a row.

| Result | Rows |
| --- | ---: |
| Source rows | 4,528,126 |
| Retained name candidates | 3,157,341 |
| Excluded provisional names | 1,139,486 |
| Excluded names containing digits | 164,003 |
| Excluded `Candidatus` names | 5,036 |
| Excluded other noncanonical shapes | 62,254 |
| Excluded `viral` flag | 6 |

The Parquet is 138,549,595 bytes. This is a **syntax filter**, not a test of
valid scientific nomenclature. Some retained one-word names may be common or
technical names, and some real taxa with hybrid signs, parentheses, Unicode,
or other notation are absent. The later correlation should keep exact matches
separate from spelling/fuzzy candidates, use rank and parent context to
resolve duplicates, and review excluded names as needed. The first local
correlation is documented in
[OTT_WIKIPEDIA_CORRELATION_RUN.md](OTT_WIKIPEDIA_CORRELATION_RUN.md).

For a later automated run, the intended stages are: copy this pinned source
URL into Blob Storage (test Azure `Put Blob From URL` against this source),
convert the stored TSV to Parquet, then correlate its names with names
extracted from the Wikipedia taxoboxes. The local scripts implement the last
two stages for investigation; no cloud orchestration has been added.
