# Compact image rights and rendition state

## Intent and scope

Keep Azure SQL small enough for the Basic database while retaining queryable
image work state and broad license statistics. This design replaces the empty
`catalog.ImageRights` and `catalog.Rendition` definitions before their data is
migrated. It does not change the already loaded image identities or the
existing Azure Tables application before cutover.

The same migration stage also converts all SQL text columns to `VARCHAR` under
the database's UTF-8 collation and evaluates `PAGE` compression. These changes
apply to the populated catalog as well as the new compact image tables.

The 400,545 catalog image identities come from page image references, not every
file in the Wikimedia image dumps. Full `imageinfo` responses and image bytes
remain in Blob Storage. SQL holds only the fields required to schedule work,
show counts and sizes, and select images by license category.

## Approaches considered

- A separate wide `ImageRights` row and full `Rendition` row per image and
  width retain all details in SQL, but repeat license strings, Blob paths,
  checksums, and recipe text. The initial schema has this form and both tables
  are empty.
- One bitmask row per image is smallest, but it cannot naturally hold each
  width's byte count and complicates per-width work queries.
- **Chosen:** a license category ID on `Image`, and one compact rendition row
  per image and immutable shape. This keeps exact per-width byte totals and
  simple work queries without repeating text or checksums.

For pending rendition work, rows could be created on demand or materialized
for every image and current shape. **Chosen:** create the rows upfront so
pending work is explicit and indexable. New images and new enabled shapes get
their missing cross-product rows in bounded bulk batches.

## License categories

`LicenseCategory` is a small lookup table with a `TINYINT` key, stable code,
and display name. Initial categories are CC BY, CC BY-SA, CC0, public domain,
other reported license, and missing license. Exact version, jurisdiction,
license URL, and attribution are not columns in SQL. Classification uses the
reported imageinfo license label; unusual labels such as GFDL are counted as
other until a category is deliberately added. Categories are statistical
labels, not a legal determination about reuse.

`Image.LicenseCategoryId TINYINT NULL` references the lookup. `NULL` means
imageinfo has not been successfully checked; the explicit missing category
means it was checked but no license label was reported. A nonempty label that
does not match a named category maps to other reported license. A failed
lookup leaves the ID null and records its error and retry in operational work
state. A later metadata refresh updates the category. Existing
`Image.MetadataStale` controls when a completed lookup should be repeated.
License statistics cover imported images only: join to a completed `w250`
rendition and count null categories separately as not yet classified. Preview
or import may fetch imageinfo for an image before its copy completes; that
category is stored but does not enter imported-image statistics yet. There is
no catalog-wide imageinfo pass for the remaining candidates.

The full imageinfo snapshot stays in Blob. The future SQL-backed writer uses a
deterministic Blob name based on `ImageId`, so SQL needs no per-image raw path.
During migration, copy or otherwise retain the latest existing snapshot for
each migrated image and verify the new location before removing a prior
reference. Blob versioning and soft deletion protect replaced snapshots.
The temporary control gallery after SQL cutover shows the broad category and
other SQL fields, without attribution or an exact license link. A later viewer
can load the snapshot when it needs those details.

## Rendition shapes and state

`RenditionShape` has an immutable `TINYINT` ID, unique path key such as
`w250`, width, output format/source kind, and any processing recipe needed by
the worker. The initial shapes are Wikimedia `w250` and derived WebP `w20`,
`w40`, `w60`, and `w120`. A genuinely different output gets a new ID and path
key; shape definitions are not edited in place. Existing image Blob names
follow `images/creatures/{shape-key}/{imageId}`, where `images` is the
container.

`Rendition` has `(ImageId INT, ShapeId TINYINT)` as its primary key, nullable
`Bytes INT`, and three `BIT NOT NULL` flags: `Complete`, `Stale`, `Failed`.
The three bits share one SQL byte. An absent row is an adoption gap, not the
normal pending state. A row with all flags false is pending. `Complete` means
a previously verified Blob exists; `Bytes` is its byte count. `Stale` means it
needs replacement. `Failed` means the latest attempt failed. These flags can
coexist: an older verified Blob can remain complete and stale after a failed
refresh. A successful replacement sets complete, clears stale and failed, and
updates bytes. A missing or invalid Blob clears complete and bytes and makes
the rendition eligible for repair. A check constraint requires bytes exactly
when complete and nonnegative bytes when present.

The current workflow processes thumbnails below 16 MiB, so signed 32-bit
`Bytes` covers these shapes. A future original-image shape must check its size
bound before using this representation. Blob metadata holds output checksum,
source checksum, and recipe marker; SQL does not repeat them. The worker must
verify Blob content and metadata before marking a rendition complete. Failure
details, running state, leases, retry times, and history live in operational
work and attempt tables, not in `Rendition`.

Source `w250` is a prerequisite for derived shapes. The scheduler filters
pending or stale rendition rows by enabled work kind, image priority, minimum
priority, retry state, source readiness, and image eligibility. Unsupported,
ambiguous, or unresolved source images can have precreated pending rows but
must be blocked from copy scheduling until their metadata changes. The work
query may use a filtered index, subject to measurement on Basic SQL. Category and rendition
aggregates come from SQL, including exact sums of completed rendition bytes.

## Migration and validation

1. Record baseline row counts, allocated and reserved MiB by table and index,
   compression setting, and representative query times. Measure maximum UTF-8
   byte lengths for every populated text column. Size new `VARCHAR(n)` columns
   by bytes, not by the old `NVARCHAR(n)` unit count; verify exact image-title
   comparisons with a binary UTF-8 collation.
2. Estimate `PAGE` compression savings for each clustered and nonclustered
   index, starting with `TaxonName`. Test representative compressed data and
   query/write performance. Convert the populated catalog text columns and
   rebuild selected indexes in a measured order, one table or index at a time.
   Account for the extra space required during rebuilds under Basic's 2 GiB
   limit; temporarily raise the database tier for the migration if the live
   headroom is insufficient. Do not assume every index benefits from `PAGE`.
3. Benchmark the compact schema with representative rows and indexes in an
   isolated SQL schema. Measure reserved MiB, insertion cost, and pending-work
   and subtree aggregate queries against Basic's 2 GiB and 5 DTU limits.
4. Add the compact schema as an additive migration to the already loaded
   database. Keep existing image identity IDs stable. New text columns use
   `VARCHAR` from their first creation.
5. Implement durable operational work, lease, retry, and attempt tables before
   switching image scheduling to the compact flags. Bulk-create current shape
   rows for catalog images, then map the existing Table rendition outcomes and
   Blob assets by `(source wiki, canonical file title)`. Existing Table rows
   use requested titles and title hashes, so resolve aliases, verify matches,
   and quarantine ambiguous outcomes rather than attach them to a guessed
   image ID. Verify each completed Blob before setting `Complete` and `Bytes`.
6. Map imageinfo fetched during preview or import to broad categories and
   retain its full snapshot in Blob under an ID-based name. Leave unfetched
   images null and failed lookups retryable. Verify category counts among
   imported images and spot-check unusual labels. Do not fetch imageinfo for
   the entire candidate catalog solely to populate category statistics.
7. Switch dashboard and workers only after row counts, byte totals, sample
   galleries, scheduling queries, and restart recovery agree with the old
   workflow. Confirm the final allocated database size and repeat query times
   on Basic before treating that tier as the steady-state choice. Preserve old
   Table rows and Blob names until cutover validation.

No historical full-resolution image import, curation redesign, exact license
reporting in SQL, or broad control UI overhaul is included here.
