# SQL catalog load and Basic-tier benchmark

Measured on 2026-10-25 in the `creational` Azure SQL Database, West Europe,
Basic (5 DTU, 2 GiB maximum). The sandbox service principal loaded the pinned
enwiki 2026-10-01 and OTT 3.3 catalog into schema `catalog` using `SqlBulkCopy`
and transactional source checkpoints. A run was stopped after 270,000 taxon
rows and resumed successfully with 20,000-row batches.

## Initial loaded rows and storage

| Table | Rows | Reserved MiB, including indexes |
|---|---:|---:|
| Taxon | 4,528,126 | 454.9 |
| TaxonName | 3,157,341 | 230.0 |
| Page | 503,735 | 103.9 |
| TaxonPage | 427,481 | 84.8 |
| Image | 400,545 | 137.2 |
| PageImage | 478,479 | 195.2 |

At the initial load, the data file allocated **1,296 MiB** of the **2,048 MiB**
Basic limit, leaving 752 MiB before that limit. Catalog tables reserved about
1,206 MiB. The old `ImageRights` and `Rendition` tables were empty; the compact
replacement and its current size are documented below. Operational job and
attempt tables are not yet created. This establishes catalog fit, not complete
migration fit.
Article text, markup, Parquet, imageinfo JSON, and image bytes remain in Blob.
The compact replacement is described in the
[image metadata design](superpowers/specs/2026-10-25-compact-image-metadata-design.md).

The existing license/attribution records and imported image variants have not
yet been migrated from the live Table/Blob workflow. The initial zero SQL rows
did not mean that no image rights or image files existed.

## Potential storage reductions

The 2026-10-25 SQL preflight confirmed all populated catalog indexes are
uncompressed and the data file is 1,296 MiB of the 2,048 MiB Basic maximum.
Full UTF-8 conversion scans of the largest text columns timed out on Basic, so
the migration uses byte limits that safely accommodate every value allowed by
the existing `NVARCHAR` declarations. These include `TaxonName.Name` at
`VARCHAR(3072)` and page/image titles at `VARCHAR(1536)`. Those are maximum
lengths, not allocated bytes per row. Small-table values were scanned exactly.

Before changing the column types, `sys.sp_estimate_data_compression_savings`
gave these sampled `PAGE` estimates. Re-estimate after UTF-8 conversion and
verify actual size and query performance after each rebuild.

| Index | Current used MiB | Estimated PAGE MiB |
|---|---:|---:|
| `TaxonName.PK_TaxonName` | 219.3 | 119.2 |
| `Page.IX_Page_Title` | 57.9 | 31.4 |
| `Image.UQ_Image_SourceTitle` | 51.0 | 21.7 |

The UTF-8 conversion finished on 2026-10-25 after the user scaled the
database from Basic to Standard S1. An in-flight `TaxonName` rewrite was
interrupted by the scale change and the idempotent migration resumed. All
text columns now report `VARCHAR` with UTF-8 collations, including binary
UTF-8 for the canonical image title. The page-title, image-title, and
unlinked-page-image indexes are present; catalog row counts and link/DFS
validation still match the pre-migration results. The data file allocated
1,616 MiB after conversion, while its configured maximum remained 2,048 MiB.
Before compression, `TaxonName` reserved 377.9 MiB, `Image` 199.4 MiB,
`Page` 100.3 MiB, and `PageImage` 195.0 MiB. Converting text alone therefore
did not free file space; the next measured step is rebuilding selected
indexes with compression. These current figures supersede the opening
pre-migration table for capacity planning.

The six measured indexes were rebuilt with `PAGE` compression on Standard S1,
after raising the configured maximum to 5 GiB for rebuild headroom. A sampled,
cleanly rebuilt `TaxonName` index used 3.781 MiB uncompressed and 3.258 MiB
with `PAGE`; its post-conversion live index had substantial free space from
the type rewrite. The live rebuilds completed without interruption:

| Table | Reserved MiB before PAGE | Reserved MiB after PAGE |
|---|---:|---:|
| TaxonName | 377.9 | 91.4 |
| Image | 199.4 | 57.9 |
| Page | 100.3 | 30.4 |
| PageImage | 195.0 | 72.5 |

`TaxonName.PK_TaxonName`, both `Image` indexes, both `Page` indexes, and
`PageImage.PK_PageImage` now report `PAGE`. `PageImage.IX_PageImage_Image`
remains uncompressed; the other tables were not changed. The catalog row counts,
linked references, DFS positions, and 901,014,677,035-byte source sum remain
unchanged. The Metazoa staged subtree query returned 320,503 pages and 242,696
images in 21.1 seconds on its first S1 pass and 10.5 seconds on the repeat;
these are not comparable to the earlier Basic timings as a compression-only
benchmark. The SQL data file still allocates 1,680 MiB despite the lower table
reservations, so allocated-file headroom is smaller than reserved-table
headroom. Its maximum was temporarily raised to 5 GiB on S1 for the rebuild.

The database collation is `Latin1_General_100_CI_AS_SC_UTF8`. All current SQL
text columns now use `VARCHAR` with UTF-8 collation. Canonical image-title
comparisons use an explicit binary UTF-8 collation. `VARCHAR(n)` limits bytes,
whereas the former `NVARCHAR(n)` declarations limited two-byte units; the
migration used conservative byte limits and verified that no values were lost.

`Taxon` is the largest table. After the operational migration, measure whether
all of its stored columns and indexes are needed by the actual queries; some
tree information may be derivable or represented more compactly. Also assess
pre-curating the OTT input to exclude taxons that the product will never use.
Any pruning must preserve ancestors and DFS ranges needed for subtree queries,
and should be validated against page/image coverage before changing the
currently loaded catalog. These are capacity options, not prerequisites for
the first SQL cutover.

All six source checkpoints completed. The catalog has 476,677 page/image
references linked to global image IDs. Another 1,802 have a known file title
but unresolved source wiki and therefore remain unlinked. Pages report 1,386
markup references without a file title. Validation found zero missing or
incorrect taxon/page DFS positions and zero unlinked references with a known
source wiki. The sum of recorded original image sizes is 901,014,677,035
bytes; this is source metadata, not bytes copied to the new `images` container.

## Compact image metadata migration

The legacy `ImageRights` and `Rendition` SQL tables were empty, so they were
replaced without moving any Table/Blob image state. The new lookup tables have
six license categories and five immutable shapes: `w250`, `w20`, `w40`, `w60`,
and `w120`. Every one of the 400,545 catalog images now has five pending
rendition rows. The backfill inserted **2,002,725 rows** in 1,000-image
batches; a repeat inserted zero rows. No missing image/shape pairs or
nonpending rows were found. All 400,545 license category IDs are null because
existing imageinfo state has not been migrated to SQL.

A representative isolated sample of 40,054 images and 200,270 renditions used
9.1 MiB for the rendition table and its work index. Creating the rows took
14.0 seconds on S1; counting eligible source images took 3.0 seconds, compared
with 5.1 seconds without the work index. On the full live catalog, `Rendition`
reserves **139.8 MiB** and the eligible `w250` count is 397,436, measured in
3.3 seconds on S1. The database was returned to **Basic** with a 2 GiB maximum;
the data file remains **1,680 MiB** allocated, leaving 368 MiB before any
growth. On Basic, the eligible `w250` count took 7.5 seconds and a full scan
for nonpending flags took 31.9 seconds. A combined validation query with
multiple full scans timed out after 120 seconds while Basic's data I/O metric
was near 100%. Future dashboard aggregates should use selective indexes or
stored counters rather than repeated catalog-wide scans. This capacity includes
only pending SQL rendition state; existing imported thumbnail bytes and
imageinfo snapshots remain in Blob/Table storage until a later cutover.

Final review found the image load staging table used `CHAR(40)` for a base36
SHA-1 that is usually 31 bytes. This had padded all 400,545 SQL hashes to 40
bytes. Staging now uses `VARCHAR(40)`; a bounded maintenance pass trimmed all
400,545 existing values, and a second pass changed zero rows. A sampled hash
now has 31 stored bytes and zero padded hashes remain. Rebuilding the compressed
`Image` primary index after the updates reduced that table's reservation from
97.5 to **54.4 MiB**. The data file remained at 1,680 MiB and all catalog
row/link/source-byte checks still match.

## Subtree benchmark

The query selects distinct pages by a DFS-range index on `TaxonPage`, then
distinct linked image IDs, then sums `Image.OriginalBytes`. Each stage uses a
temporary table to avoid repeating the page search. Timings include the three
stages on Basic; the first pass follows the load and the second repeats the
same query. They are not guaranteed cold/warm cache measurements.

| OTT root | Pages | Distinct images | Original bytes | First pass | Repeat |
|---|---:|---:|---:|---:|---:|
| Carnivora (44565) | 712 | 3,213 | 9,693,597,488 | 3.3 s | 0.1 s |
| Tetrapoda (229562) | 42,226 | 82,284 | 204,707,994,494 | 6.8 s | 6.3 s |
| Metazoa (691846) | 320,503 | 242,696 | 543,174,907,780 | 65.9 s | 20.8 s |

Before the DFS position was materialized on `TaxonPage`, collecting Metazoa's
page IDs alone took about 28 seconds. The indexed collection took 5.6 seconds;
the rest of the repeat query spent about 12.4 seconds finding distinct images
and 2.8 seconds summing their metadata.

**Assessment:** Basic fits the catalog and gives usable response times for
small clades. Exact subtree totals are needed on demand when previewing or
focusing an OTT branch; they do not need continuous recomputation. A rare
Metazoa-wide preview can show a loading state for roughly 20 seconds on a
repeat query. Global work counters are a separate operational-state query and
do not require this subtree join. Caching broad subtree results can wait until
observed usage justifies it. The current 368 MiB allocated-file headroom must
still be checked against operational tables before treating Basic as the
steady-state tier. [The benchmark SQL](../apps/Creational.Dataflow/Sql/catalog_benchmark.sql)
can be rerun for other roots.

## Deployment state and next work

The live dashboard and image workers still use Azure Tables. No image Blobs
were renamed or copied. The next migration stage is the operational schema and
repository, plus measurement of its rows. Cutover requires those checks and a
separate verification of existing image assets.

For local SQL work in this sandbox, set `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`
alongside `Authentication=Active Directory Default`. Without this selector,
SqlClient's default credential chain exceeded the 30-second connection timeout
despite a valid Azure CLI login. The App Service should use its own managed
identity authentication mode.
