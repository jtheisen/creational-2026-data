# SQL catalog load and Basic-tier benchmark

Measured on 2026-10-25 in the `creational` Azure SQL Database, West Europe,
Basic (5 DTU, 2 GiB maximum). The sandbox service principal loaded the pinned
enwiki 2026-10-01 and OTT 3.3 catalog into schema `catalog` using `SqlBulkCopy`
and transactional source checkpoints. A run was stopped after 270,000 taxon
rows and resumed successfully with 20,000-row batches.

## Loaded rows and storage

| Table | Rows | Reserved MiB, including indexes |
|---|---:|---:|
| Taxon | 4,528,126 | 454.9 |
| TaxonName | 3,157,341 | 230.0 |
| Page | 503,735 | 103.9 |
| TaxonPage | 427,481 | 84.8 |
| Image | 400,545 | 137.2 |
| PageImage | 478,479 | 195.2 |

The data file has allocated **1,296 MiB** of the **2,048 MiB** Basic limit
as checked on 2026-10-25, leaving 752 MiB before that limit. Catalog tables
reserve about 1,206 MiB.
`ImageRights`, `Rendition`, and operational job/attempt tables are still empty
or not yet created. This establishes catalog fit, not complete migration fit.
Article text, markup, Parquet, imageinfo JSON, and image bytes remain in Blob.
The planned compact replacement for the empty rights and rendition tables is
described in the [image metadata design](superpowers/specs/2026-10-25-compact-image-metadata-design.md).

`ImageRights` and `Rendition` have schema but no rows because this load only
migrated the catalog and source image metadata. The existing license/attribution
records and imported image variants have not yet been migrated from the live
Table/Blob workflow. Zero rows here do not mean that no image rights or image
files exist.

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

No catalog table or index currently uses SQL data compression; the live
partitions all report `DATA_COMPRESSION = NONE`. Benchmark `PAGE` compression
for text-bearing tables, especially `TaxonName`, and then `Page`, `Image`, and
`PageImage` as appropriate. Measure reserved space, rebuild time, and query and
write performance before applying it to the live Basic database. Indexes need
to be assessed separately from their tables.

The database collation is `Latin1_General_100_CI_AS_SC_UTF8`. The intended SQL
schema uses `VARCHAR` for **all text columns**, not `NVARCHAR`; this applies to
taxon names, page and image titles, collection and tree keys, rights fields,
errors, and load keys. Review fixed-width textual hashes currently declared as
`CHAR` as part of that consistency pass (or store their decoded bytes as
`BINARY`). The existing loaded schema still has `NVARCHAR` columns, so this
requires a later migration, not just a change to the initial schema script.
Keep exact image-title comparisons with an explicit binary UTF-8 collation and
update loader staging columns, casts, and parameters as needed. Check the byte
limits against real data: `VARCHAR(n)` limits bytes, whereas `NVARCHAR(n)`
limits two-byte units. Measure the resulting space savings before relying on
them for Basic-tier capacity.

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
observed usage justifies it. The 752 MiB headroom must still be checked against
rights, rendition, and operational tables before committing to Basic as the
final tier. [The benchmark SQL](../apps/Creational.Dataflow/Sql/catalog_benchmark.sql)
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
