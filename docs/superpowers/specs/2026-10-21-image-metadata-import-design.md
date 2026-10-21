# Image metadata import

Status: design agreed in conversation, 2026-10-21.

## Purpose

Import file metadata independently of original image bytes so the dashboard can
estimate original-byte totals for an English Wikipedia article or OTT subtree.
The existing parsed `image_references.parquet` files supply candidate titles;
the preview currently resolves metadata only for its visible page and cannot
calculate complete totals. This stage does not copy image originals or make a
licensing decision.

## Sources and versioning

Pin the dated `enwiki-20261001-image.sql.gz` and
`commonswiki-20261001-image.sql.gz` source files from Wikimedia's SQL dump
runs, with their source URLs, lengths, and checksums. Never use the mutable
`latest` URLs as a target identity. The existing article XML shards come from
`mediawiki_content_current`; their shared date label is a collection label,
not a promise that the separate SQL dumps are simultaneous snapshots.

Add a source-set pseudo-target that records the exact article and image-dump
pins making up this collection. Keep the existing article `source-manifest`
identity and adopted outputs intact. Source-set changes must not invalidate
article transforms merely because an unrelated SQL pin changed. The dashboard
groups these pins as one source version and shows their individual dates and
checksums.

The live `CollectionTargets` table was checked on 2026-10-21: all 83 existing
targets in `enwiki/2026-10-01` had complete outcomes, with no active attempt or
unsatisfied stale revision. Adding this feature is an additive graph migration.
Do not change their target IDs, recipe versions, content dependencies, readiness
gates, fingerprints, manifests, or Table rows during rollout. The source-set
target can reference the existing article manifest for presentation, but it
must not become a new content dependency of any existing target. Add only the
new image targets and their source pins. A preflight plan must verify that all
83 prior targets still resolve to `Current`; abort the image import rollout if
the new graph would schedule any of them. New targets begin as missing and run
from their own inputs. If a needed image artifact was produced manually before
rollout, adopt it only after verifying its members and provenance, using the
existing non-destructive adoption rules.

## Target graph

| Target | Content inputs | Verified output |
| --- | --- | --- |
| `enwiki-image-dump` | Pinned English Wikipedia SQL image dump | Copied `.sql.gz` Blob |
| `commons-image-dump` | Pinned Commons SQL image dump | Copied `.sql.gz` Blob |
| `enwiki-image-metadata` | `enwiki-image-dump` | Complete local-file metadata Parquet and report |
| `commons-image-metadata` | `commons-image-dump` and all current parsed `image_references.parquet` outputs | Candidate-filtered Commons metadata Parquet and report |
| `resolved-image-metadata` | Both metadata Parquets and parsed image references | One candidate-to-file resolution Parquet and coverage report |

The English Wikipedia conversion retains its entire small image table, so it
does not depend on article parsing. The Commons conversion uses the distinct
resolvable candidate titles across parse shards as its filter; changed titles
invalidate that target. The final resolution target chooses a local English
Wikipedia file when one exists for a candidate title, otherwise the Commons
file. It preserves missing and ambiguous candidates as explicit statuses.
The current gallery's Commons-first metadata lookup must be corrected to use
this precedence before it is treated as a reliable view of the selected file.

The SQL source files are compressed mysqldump streams containing schema and
bulk `INSERT` statements. A Container Apps worker parses them sequentially,
without loading them into MySQL or staging an uncompressed copy on disk.
Wikimedia dump checksums and the source-set pins verify the copied bytes.
Workers write attempt-scoped Parquet artifacts and reports; the control plane
publishes completion markers only after verification, following the existing
target publication protocol. Stale marking and reruns use the existing graph
planner. A partial conversion leaves the previous verified artifact current.

## Metadata and aggregate semantics

Keep source wiki, canonical filename/title, original byte count, width,
height, MIME type, upload timestamp, and SHA-1 when present in the SQL dump.
The image SQL table is keyed by filename; it does not supply the file-page
integer ID. Page IDs can be added through a separately pinned `page.sql.gz`
source or selective API enrichment later and are not required for byte totals.
Do not claim that title matching alone is a permanent identity across renames;
retain source wiki and source version with each row.

The dashboard joins resolved candidate metadata to selected article references
through DuckDB and sums each distinct resolved source file once per selection.
It reports the known original-byte total alongside resolved, missing, and
unresolved-reference counts. The total is complete only when every relevant
candidate is resolved for the pinned metadata snapshot. Child totals cannot
be added blindly to obtain a parent total because a file can appear in
multiple branches. The OTT-to-Wikipedia correlation is still investigative,
so subtree coverage is a property of the current correlation, not proof that
every lifeform page has been found.

## Storage boundaries and validation

Store the copied SQL dumps and versioned Parquet output groups in Blob Storage.
Azure Tables contains target state, attempts, staleness, and current artifact
pointers; it does not contain one row per image for this metadata import.
Per-image operational state belongs to a later original-copy workflow.

Validate the SQL parser against sampled rows and the dump's declared column
order, including escaped filenames and large integers. Confirm Parquet schema,
Snappy compression, row counts, source fingerprints, and deterministic handling
of duplicate references. Test local-file precedence, missing files, and
multi-article deduplication in aggregate queries. The first live run should
compare a small sample with Wikimedia `imageinfo` before treating totals as
reliable; metadata API calls should follow Wikimedia's request limits.
