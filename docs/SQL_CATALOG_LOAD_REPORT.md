# SQL catalog load status

The loader and Basic-tier benchmark are prepared on the `feat/sql-catalog` branch.
No live SQL load or tier measurement has been run yet. The sandbox's broader
Azure login was logged out after it repaired the database users. The remaining
load can use the sandbox identity's ordinary database and Blob access.

## Prepared inputs

The locally pinned Parquet projections were checked on 2026-10-24:

| Projection | Rows |
|---|---:|
| Full OTT edge tree | 4,528,126 |
| Taxon names | 3,157,341 |
| Wikipedia pages | 503,735 |
| Distinct resolved image identities | 400,545 |
| Image references with a file title | 478,479 |

The checks also found no duplicate taxon names, duplicate taxon/page links,
duplicate image source/title keys, or orphan page/taxon references in the
projected data. References without a source wiki retain their file title and
will have a null image ID. References without a file title contribute to each
page's unresolved count.

## Next checkpoint

1. Run the SQL integration tests with `CREATIONAL_SQL_TEST_CONNECTION_STRING`
   using the sandbox identity's narrow SQL login. These tests create and remove
   isolated schemas. The ordinary test suite does not execute the SQL cases
   without that environment variable.
2. Run `--load-sql-catalog` on a machine with the manifest-verified local
   Parquet snapshot and the taxonomy names file. Set
   `CREATIONAL_SQL_CONNECTION_STRING` and optionally
   `CREATIONAL_SQL_CATALOG_SCHEMA` (default `catalog`). The command arguments
   are `<snapshot-root> <taxonomy-names.parquet> <collection-id>
   <collection-key> <tree-id> <tree-key>`.
3. Run [catalog_benchmark.sql](../apps/Creational.Dataflow/Sql/catalog_benchmark.sql)
   and record actual rows, allocated/used MB, and cold/warm Carnivora query
   timings here. Compare the result with the 2 GB/5 DTU Basic tier before
   wiring SQL into the live dashboard.

The current Table-backed dashboard remains the active control plane.
