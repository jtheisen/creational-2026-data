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

`ImageRights` and `Rendition` have schema but no rows because this load only
migrated the catalog and source image metadata. The existing license/attribution
records and imported image variants have not yet been migrated from the live
Table/Blob workflow. Zero rows here do not mean that no image rights or image
files exist.

## Potential storage reductions

No catalog table or index currently uses SQL data compression; the live
partitions all report `DATA_COMPRESSION = NONE`. Benchmark `PAGE` compression
for text-bearing tables, especially `TaxonName`, and then `Page`, `Image`, and
`PageImage` as appropriate. Measure reserved space, rebuild time, and query and
write performance before applying it to the live Basic database. Indexes need
to be assessed separately from their tables.

The database collation is `Latin1_General_100_CI_AS_SC_UTF8`. Before loading
`ImageRights`, change its `LicenseShortName` and `Attribution` columns from
`NVARCHAR` to `VARCHAR` so those fields use UTF-8 storage. Check the byte limits
against real metadata: `VARCHAR(n)` limits bytes, whereas the current
`NVARCHAR(n)` limits two-byte units. The existing `LicenseUrl` and
`RawImageInfoPath` columns are already `VARCHAR`.

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
