# OTT 3.3 to Wikipedia taxobox correlation: local investigation

`correlate_ott_wikipedia.py` combines the October 2026 parsed Wikipedia
taxoboxes with the locally converted [OTT 3.3 taxonomy](OTT_TAXONOMY_RUN.md).
Run it from the project root with:

```sh
.venv/bin/python -u correlate_ott_wikipedia.py
```

The current output is under the ignored
`results/ott-wikipedia-correlation-v3/` directory. Earlier `v1` and `v2`
were superseded while improving wiki style-markup handling and adding the
near-name audit. This is an investigative correlation, not an accepted taxon
crosswalk.

For each confirmed taxobox, the script prefers the explicit `taxon`
parameter, then genus/species components (including subspecies or variety for
infraspecies boxes), then legacy binomial/trinomial fields. It strips simple
wiki display markup but refuses fields requiring template expansion. It uses
the article title only for two Automatic taxoboxes lacking `taxon`, recording
that weaker method. Virus, hybrid, paraphyletic-group, population, trace-fossil,
and egg-fossil boxes are held out of this first name match. The proposed name
must pass the same conservative syntax filter as OTT. Original parameter
positions remain attached so a result can be traced to the raw taxobox data.

| File | Grain | Rows | Bytes |
| --- | --- | ---: | ---: |
| `wiki_names.parquet` | One proposed name or review reason per taxobox | 503,010 | 28,527,100 |
| `exact_matches.parquet` | One box–OTT ID pair per case-folded exact name match | 427,482 | 25,432,944 |
| `match_status.parquet` | One box with exact-match count and status | 503,010 | 24,366,279 |
| `near_name_candidates.parquet` | Review-only candidate pair | 8,572 | 467,943 |

`report.json` records counts by method, review reason, match status, and
near-name route. Join files by `(page_id, revision_id, box_index)`. The OTT
table retains `ott_id` and parent/rank/source context. Repeated OTT names are
not collapsed: a box can have multiple exact OTT candidates.

| Exact-match status | Boxes |
| --- | ---: |
| Exactly one OTT ID with this name | 421,485 |
| Multiple OTT IDs with this name | 2,853 |
| Usable name, no exact OTT match | 67,084 |
| No usable name from this pass | 11,588 |

Thus 424,338 boxes (84.4%) have at least one exact name match. This measures
name overlap, **not verified identity**. Same-name homonyms, different ranks,
synonyms, and taxonomic rearrangements still need validation. The OTT source
was last modified in 2021 while the Wikipedia snapshot is from 2026, so a
missing exact match is not necessarily a spelling error.
Those exact pairs involve 426,995 distinct OTT IDs out of the 3,157,341
retained OTT name candidates; the Wikipedia side here is only the selected
taxobox candidate pool, not all encyclopedia pages.

The near-name pass considers only unmatched **binomials**. It blocks on an
identical genus and checks the epithet, or blocks on an identical epithet and
checks the genus. It retains edit distances one and two: 6,302 candidate
pairs differ in epithet, 2,270 in genus, covering 7,009 distinct boxes.
Examples include `Begonia carolineifolia` versus OTT
`Begonia carolinifolia`, and `Mangelia diatula` versus OTT
`Mangilia diatula`. These are review suggestions, not asserted equivalences;
edit distance two can produce unrelated taxa. Uninomial and infraspecific
near-misses are not searched yet.

The 11,588 no-name boxes include 2,491 deliberately held-out families,
8,607 names that do not fit the syntax rule, 427 with no identity fields,
and small numbers needing markup expansion or other review. Wikipedia
disambiguators such as `Zea (plant)` in a genus field are deliberately not
removed automatically. A later matching stage should resolve wiki taxonomy
lookup keys, use rank and parent context, and distinguish accepted names from
synonyms before promoting candidate pairs to links.
