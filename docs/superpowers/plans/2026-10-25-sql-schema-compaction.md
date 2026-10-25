# SQL Schema Compaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the populated catalog to UTF-8 `VARCHAR`, apply measured page compression, and install the compact image metadata schema before image-state cutover.

**Architecture:** Keep the live Table/Blob application running while SQL changes are measured and applied one object at a time. Use additive, resumable migrations for the populated catalog and an asserted-empty replacement for the unused rights/rendition tables. Benchmark the new schema with representative rows; a later plan migrates operational state and switches the application.

**Tech Stack:** Azure SQL Database Basic, .NET 10, Microsoft.Data.SqlClient, SqlBulkCopy, xUnit.

**Spec:** [Compact image rights and rendition state](../specs/2026-10-25-compact-image-metadata-design.md).

## Global Constraints

- The live `creational` database uses `Latin1_General_100_CI_AS_SC_UTF8`, Basic 5 DTU, and a 2 GiB data-file cap.
- All SQL text columns, including currently `NVARCHAR` and fixed `CHAR` values, use `VARCHAR` under a UTF-8 collation; exact canonical image-title comparisons retain binary semantics.
- New `RenditionShape` IDs are immutable `TINYINT`s for `w250`, `w20`, `w40`, `w60`, and `w120`.
- `Rendition` contains `(ImageId, ShapeId)`, nullable 32-bit `Bytes`, and `Complete`, `Stale`, `Failed` bits; no checksums or per-row recipe.
- `Image.LicenseCategoryId` is nullable `TINYINT`; license statistics count completed `w250` images only.
- No live app cutover or deletion of old Table rows/Blob paths occurs in this plan.
- Apply live catalog changes only after UTF-8 length and rebuild-space checks; record a rollback/retry point after each object.

## Review Focus

- A non-ASCII taxon or page name whose UTF-8 byte count exceeds the old `NVARCHAR(n)` value must migrate without truncation; Task 2 tests it.
- Two distinct canonical titles must remain distinct under the new binary UTF-8 comparison; Task 2 tests it.
- A failed or interrupted index rebuild must leave already migrated objects identifiable and allow a restart; Task 3 tests migration state.
- An unsupported image with precreated rendition rows must not be treated as ready to copy by later work queries; Task 4 tests the eligibility query.
- A completed stale rendition after a failed replacement must retain the prior byte count; Task 4 tests the constraint and update semantics.

---

## File map

- `apps/Creational.Dataflow/Sql/SqlCatalogCapacity.cs`: baseline sizes, UTF-8 bounds, compression estimates, and post-change checks.
- `apps/Creational.Dataflow/Sql/Migrations/001_catalog.sql`: correct schema for fresh installs.
- `apps/Creational.Dataflow/Sql/Migrations/003_utf8_catalog.sql`: conversion of the already loaded catalog, with explicit index handling.
- `apps/Creational.Dataflow/Sql/Migrations/004_page_compression.sql`: selected per-index compression operations after measurements.
- `apps/Creational.Dataflow/Sql/Migrations/005_compact_images.sql`: compact shape, license, and rendition schema.
- `apps/Creational.Dataflow/Sql/SqlCatalogSchema.cs`: resource rendering and migration entry points.
- `apps/Creational.Dataflow/Sql/SqlCatalogLoader.cs`: UTF-8 staging and parameters; bounded shape-row creation.
- `apps/Creational.Dataflow.Tests/SqlCatalogSchemaTests.cs`: isolated-schema migration and data preservation tests.
- `apps/Creational.Dataflow.Tests/SqlCatalogLoaderTests.cs`: new-image shape-row and restart tests.
- `docs/SQL_CATALOG_LOAD_REPORT.md`: measured before/after space and query timings.

### Task 1: Capacity and UTF-8 preflight

**Interfaces:** Produce `SqlCatalogCapacity.ReadAsync(SqlConnection, string schema, CancellationToken)` returning per-object reserved/used MiB, compression mode, and maximum projected UTF-8 bytes for populated text columns; `EstimatePageAsync` returns per-index current and estimated KiB. Later tasks use the report as a live gate.

- [ ] Add a SQL integration test in `SqlCatalogSchemaTests.cs` with ASCII, accented, CJK, and supplementary-character values; assert the preflight reports UTF-8 byte lengths rather than character counts.
- [ ] Run `dotnet test apps/Creational.Dataflow.Tests/Creational.Dataflow.Tests.csproj --filter FullyQualifiedName~SqlCatalogSchemaTests`; confirm the new test fails before implementation.
- [ ] Implement the preflight in `SqlCatalogCapacity.cs` using `sys.dm_db_partition_stats`, `sys.partitions`, `DATALENGTH(CONVERT(VARCHAR(MAX), column COLLATE Latin1_General_100_CI_AS_SC_UTF8))`, and `sys.sp_estimate_data_compression_savings` per index. Report database file size and max size separately from reserved table size.
- [ ] Run the focused test and a read-only live preflight. Record baseline rows, allocated MiB, UTF-8 byte maxima, estimated `PAGE` savings, and current representative query times in `docs/SQL_CATALOG_LOAD_REPORT.md`.
- [ ] Commit the probe and baseline report.

### Task 2: UTF-8 `VARCHAR` schema and conversion

**Interfaces:** `SqlCatalogSchema.RenderUtf8Conversion(schema)` renders `003_utf8_catalog.sql`; the conversion is restartable per table/index and verifies no value exceeds its destination byte length. Fresh `Render(schema)` creates equivalent `VARCHAR` columns. `SqlCatalogLoader` stages canonical titles as UTF-8 `VARCHAR` and uses compatible binary comparison.

- [ ] Add isolated-schema tests that load non-ASCII names and binary-distinct image titles into the old schema, run the conversion, and assert values, row counts, links, title uniqueness, and `VARCHAR`/UTF-8 metadata are preserved. Repeat the migration to test restart behavior.
- [ ] Run the focused tests and confirm they fail before the new migration exists.
- [ ] Update `001_catalog.sql` for fresh installs, then add `003_utf8_catalog.sql`. Convert both `NVARCHAR` and textual `CHAR` declarations to `VARCHAR`. Determine destination byte lengths from Task 1; drop and recreate affected indexes/constraints around indexed column conversions. Change the explicit canonical-title collation to binary UTF-8. Keep the current global `ImageId`s and DFS indexes.
- [ ] Update `SqlCatalogLoader.cs` staging and string parameters. Run the SQL schema and loader tests, then the whole .NET test project.
- [ ] Apply the live conversion one measured object at a time. If Basic lacks safe rebuild headroom, temporarily scale for the maintenance window and return to Basic after validating the final size. Confirm every converted column's data type/collation, count, maximum byte length, and dependent index.
- [ ] Commit the migration and measured report.

### Task 3: Selective page compression

**Interfaces:** `SqlCatalogSchema.RenderPageCompression(schema)` renders `004_page_compression.sql`; its operations are repeatable and target named indexes only when Task 1 measurements justify `PAGE` compression. Clustered and nonclustered indexes have independent settings.

- [ ] Add isolated-schema tests that populate a text-heavy `TaxonName`, run the chosen compression migration twice, and assert `sys.partitions.data_compression_desc`, row counts, and query results. Include a simulated midway restart by running the first operation separately.
- [ ] Run the focused tests and confirm they fail before implementation.
- [ ] Add `004_page_compression.sql` and renderer; start with `TaxonName` and include other indexes only where the estimate shows material savings without unacceptable benchmark regression. Keep each index operation separately executable and record its completion.
- [ ] Run tests. Apply each live index rebuild after checking free/rebuild space; record before/after reserved MiB, elapsed time, DTU pressure, and representative subtree/title-query times. Pause and adjust tier or ordering if the Basic limit or workload becomes a concrete risk.
- [ ] Commit the compression migration and updated report.

### Task 4: Compact image metadata schema

**Interfaces:** `SqlCatalogSchema.RenderCompactImages(schema)` renders `005_compact_images.sql`. The migration asserts the old `ImageRights` and `Rendition` tables are empty before replacing them, creates `LicenseCategory`, `RenditionShape`, and compact `Rendition`, and adds nullable `Image.LicenseCategoryId`. `SqlCatalogLoader.EnsureRenditionsAsync` inserts missing image/shape pairs in bounded batches without resetting existing flags.

- [ ] Add isolated-schema tests for exact shape IDs/keys, category IDs, `Bytes`/`Complete` constraint, allowed complete+stale+failed state, and repeatable cross-product creation. Test that unsupported images are excluded by the later eligible-work query despite having rows.
- [ ] Run the focused tests and confirm they fail before implementation.
- [ ] Add the migration, renderer, and bounded row creator. Seed immutable shape/category definitions in migration SQL; use `VARCHAR` for all text. Update isolated-schema test cleanup for the new foreign-key order. Ensure a repeated run never resets completed or stale rows.
- [ ] Run focused and full .NET tests. Load representative cross-product rows into an isolated schema, measure reservation and pending-work query performance, then apply the empty-table replacement and row creation to live SQL using checkpoints.
- [ ] Record live row counts and storage by table/index, confirmed license-null counts, and remaining Basic headroom. Commit the compact schema and report.

## Handoff boundary

This plan leaves Azure Tables as the live operational source. A follow-on plan
must create durable work/lease/retry/attempt tables, migrate and verify old
imageinfo and rendition assets through canonical image IDs, update the control
gallery, and cut over workers. It must recheck Basic capacity with those rows.
