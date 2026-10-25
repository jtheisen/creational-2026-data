# SQL operational cutover

The SQL catalog is the operational store for collection targets, image work, and
rendition completion. Blobs still hold the large content, imageinfo snapshots,
resized images, and bounded worker manifests. The old Azure Table rows and
`wikimedia-extracts/thumbnails` Blobs are retained during validation.

## Schema and adoption

Set `CREATIONAL_SQL_CONNECTION_STRING` to an Azure SQL connection string using
Microsoft Entra authentication. `CREATIONAL_SQL_CATALOG_SCHEMA` defaults to
`catalog`. The idempotent migrations are:

```sh
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-operational
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-image-work
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-image-adoption
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-resize-batch
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-source-generation
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-pending-rendition-indexes
dotnet run --project apps/Creational.Dataflow -- --migrate-sql-focused-image-priority
```

Copy and compare collection state with `--adopt-sql-targets --apply`. Run
`--adopt-sql-targets --verify-only` again before switching the control app.
`--adopt-sql-images --verify-only 999999` reports mapping coverage; the `--apply`
form copies source metadata and renditions in bounded, concurrent, verified
100-row windows. Its SQL checkpoints let an interrupted command resume. Each
destination is `images/creatures/w{width}/{ImageId}`, and snapshots are
`images/metadata/{ImageId}.json`. An unmatched or invalid legacy row goes to
`ImageAdoptionIssue`; a transient SQL or Blob failure retries from the last
checkpoint. The old asset is not deleted. Run `--sql-adoption-issues` for recent
exceptions. Check destination Blob metadata and SQL `Rendition` flags before
considering the migration complete.

The app, worker, and adoption identity require permission to delete previous
Blob versions on the `images` container for old versions to be soft deleted
after verified replacement. The built-in Storage Blob Data Owner role supplies
that permission; Storage Blob Data Contributor does not. The account currently
retains soft-deleted versions for seven days. A cleanup failure is logged and
does not change a verified publication into failed work.

## Control app

Use `SqlOperational__Enabled=true` and
`SqlOperational__ConnectionString=<connection string>` to switch collection
targets and settings to SQL. `SqlOperational__ImageEnabled=true` also stops the
legacy Table-backed image and resize hosted services, guards their creation
methods, and starts SQL metadata, copy, and resize dispatchers. All three
`ImageWorkSetting.Enabled` flags default to false; turning on the app setting
does not start image imports by itself.

`--sql-work-status` lists settings, queued work, and open resize batches.
`--sql-image-work-setting Metadata|Copy|Resize` reads a kind's setting. The
write form takes `KIND ENABLED MINIMUM_PRIORITY CONCURRENCY PACING_MS`.
`--sql-prioritize-image ID PRIORITY` or `--sql-prioritize-ott OTT_ID PRIORITY`
changes the focus. Higher priority runs first; default priority and minimum
are zero. Priority below **1** is always suspended, even when the configured
minimum is zero. The effective threshold is the larger of 1 and the setting's
minimum priority. An attempt already running when priority drops may finish.
Existing queue priorities change in the same SQL operation.
`--sql-mark-image-metadata-stale ID` schedules a metadata refresh and reopens
a blocked metadata item. Changing an image's priority also reopens its blocked
items for an explicit retry.
`--sql-mark-image-source-stale ID` queues one verified source rendition for
replacement and rejects the request while its copy lease is active.

New metadata results enqueue source copies, and verified source copies enqueue
one resize item per image. The enabled copy and resize dispatchers also find
previously adopted pending renditions in bounded windows. `--sql-enqueue-pending
Copy N` and `--sql-enqueue-pending Resize N` can seed a window manually without
enabling the kind. Each queue is capped relative to its concurrency, so the
dispatchers materialize only a small number of eligible rows per cycle.
Positive minimum priorities use an indexed image-priority seek; a zero minimum
uses pending-rendition indexes so an idle resize cycle does not scan completed
source images.

## Worker and recovery

The Python collection worker remains stateless. The queue dispatcher uses a
SQL point read for its target. SQL rowversion protects transitions and stores
attempt history. The Python resize worker accepts
`--sql-manifest sql-resize/manifests/<batch-id>.json`, reads ID-based source
Blobs, and writes a result marker to `sql-resize/results/<batch-id>.json`.
The control app verifies each derived Blob's length, SHA-256, source lineage,
recipe, and content type before updating SQL. Successful shapes are retained
when another shape fails; the remaining work is retried.

Unknown Container Apps launch outcomes remain marked uncertain and are not
relaunched automatically. Inspect the execution and result marker before
releasing such claims. Expired resize claims without a batch record are
recovered automatically. The status of a Container Apps execution alone is
never treated as proof that its output was published.

The smallest live test is one image at a priority above the effective
threshold, with concurrency one. Keep the other kinds disabled until that
stage's Blob and SQL attempt have both been verified. Observe Azure SQL file
allocation with `--sql-capacity`; the current Basic database limit is 2 GiB.

## HTTP control API

The existing App Service authentication covers `/api/v1` as it does the
dashboard. Every mutating request must include `X-Creational-Control: 1`;
this header prevents cross-site form submissions, and no cross-origin browser
access is configured. The API currently manages the pinned enwiki collection.
Requests and responses are JSON; mutating routes return 400 for invalid input, 404 for
unknown resources, and 409 for an in-flight source-copy conflict.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/collections/enwiki-2026-10-01` | Plan, target state, recent runs, parallelism |
| POST | `/api/v1/collections/enwiki-2026-10-01/imports` | Start or get an active import |
| PUT | `/api/v1/collections/enwiki-2026-10-01/settings/parallelism` | Set `{"value":1}` through the existing setting repository |
| POST | `/api/v1/collections/enwiki-2026-10-01/targets/{kind}/{key}/stale` | Mark one defined target stale |
| GET | `/api/v1/images/work` | Settings, queue counts, open resize batches |
| PUT | `/api/v1/images/work/settings/{kind}` | Set `enabled`, `minimumPriority`, `concurrency`, `pacingMilliseconds` |
| PUT | `/api/v1/images/{imageId}/priority` | Set `{"value":1}` |
| PUT | `/api/v1/images/work/ott/{ottId}/priority` | Set subtree priority and return affected image count |
| POST | `/api/v1/images/{imageId}/metadata/stale` | Request metadata refresh |
| POST | `/api/v1/images/{imageId}/source/stale` | Request replacement of an existing w250 rendition |

`{kind}` for image work is `Metadata`, `Copy`, or `Resize`. Image routes return
404 when SQL image operations are disabled. A future same-origin UI can call
these routes without reading SQL or Azure Tables directly.
