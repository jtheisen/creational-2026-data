# Collection import operations

The dataflow app lives in `apps/Creational.Dataflow`. It is a .NET 10 Blazor
Server control plane for the pinned `enwiki/2026-10-01` collection. The app
lists target groups, explains why each is current or would run, lets an operator
mark a group stale, and starts the import. The collection graph includes OTT
3.3 conversion, the investigative OTT–Wikipedia correlation, and dated
Wikimedia image-metadata dumps. The image preview reads those outputs and can
start a bounded 250px thumbnail import for its current page; see
[image preview operations](IMAGE_PREVIEW_OPERATIONS.md).

## First thumbnail import

The image preview's import button snapshots at most 24 visible file titles
into a job row in `CollectionTargets`. The hosted thumbnail importer processes
one file at a time and resumes pending or running jobs after an App Service
restart. Asset rows are neutral (`unreviewed`); width-specific rendition rows
record source URL, Blob path, bytes, SHA-256, and per-file errors. These rows
are separate from the collection dependency graph. The job page shows each
file's state and the completed byte total. The shared Wikimedia request pacer
starts at one request per second and backs off on 429/503.

The Blob path is `thumbnails/250px/<hash-prefix>/<title-hash>/<filename>` in
`wikimedia-extracts`. A verified current rendition is skipped by later jobs.
When a rendition is marked stale, a replacement is uploaded and verified before
the prior version is deleted under Blob soft-delete retention. That exact-version
delete requires `Storage Blob Data Owner` on the container; the App Service
identity has this role. This first slice does not yet expose a stale toggle or
bulk subtree import.

For a one-file operational canary from a machine with Azure CLI data roles:

```sh
AZURE_TOKEN_CREDENTIALS=AzureCliCredential dotnet run --project apps/Creational.Dataflow -- --import-thumbnail-once 'File:Cat - 03.jpg'
```

On 2026-10-23, the canary copied the Commons image as an 18,311-byte
`image/jpeg` Blob with SHA-256
`c2842946387735f4ebb2d33b8951edcf127594a33a746f523e86187c075a396f`.
A second one-file job completed without changing the Blob ETag or version ID.

The dashboard plan uses the last verified Table state for a quick preview.
Import rechecks the referenced Blob groups before it queues any work. This is
especially relevant for legacy Blobs without SHA-256 metadata, whose content
must be streamed to verify them; a cold full verification took about 70 seconds
on the development VM after bounded parallelization.

## Existing-output bootstrap

The 19 dump shards, alias indexes/seed, and candidate-pool groups already live
in Azure. The 19 parsed shard groups, OTT source and names, and correlation
group were published to new immutable Blob prefixes by
`tools/upload_existing_results.py`. That tool checks report lengths, uploads
members, verifies destination length and SHA-256 metadata, and writes each
`_COMPLETE.json` last. It is idempotent and refuses a differing existing marker.
To inventory locally:

```sh
python3 -m tools.upload_existing_results --check
```

On the VM, after transferring the local outputs and using its Python venv:

```sh
cd ~/creational-build
PYTHONPATH=$HOME ~/wikimedia-venv/bin/python -m tools.upload_existing_results --apply
```

The .NET adopter then normalizes legacy output manifests, verifies all members,
and writes one target row per graph node to Azure Tables. It does not run any
transform. Verify with Blob access only, then apply with Table access:

```sh
dotnet run --project apps/Creational.Dataflow -- --adopt-existing --verify-only
dotnet run --project apps/Creational.Dataflow -- --adopt-existing --apply
```

The VM's SDK is at `~/duckdb-net-probe/dotnet/dotnet` if `dotnet` is not on
`PATH`. The adopter needs `Storage Table Data Contributor`; a Blob role alone
is insufficient. The Table name is `CollectionTargets`. An all-current first
import should schedule no expensive work.
Run `--apply` only for initial bootstrap or a missing target row. If a tracked
target has since been marked stale or regenerated, the adopter refuses to
replace its Table pointer with an older bootstrap group.

## Stale and retry semantics

`Mark stale` increments only the selected target's Azure Table revision. The
planner blocks descendants until it sees the new verified output fingerprint.
When the rebuilt bytes are unchanged, descendants stay current. A parser shard
depends on its corresponding pool shard and the alias seed; the pool finalizer
is a readiness gate. Generated output groups use attempt prefixes, preserving
the old verified group until the new completion marker is published.

External-source transfers run in the App Service's hosted copier with bounded
block buffers and at most five concurrent requests; Wikimedia image requests
are limited to one until measured otherwise. Live bytes are in a singleton
memory ledger. Refreshing the page keeps the transfer visible, while an App
Service restart abandons it. A later Import sees the incomplete Table attempt
and can retry. A source Blob replacement is committed only after stream length
and the pinned source checksum match. Article dumps use SHA-256; the Wikimedia
SQL image dumps publish SHA-1, and the copier computes SHA-256 for Blob
verification while copying them. The commit uses the captured current ETag;
after the new current Blob verifies, the exact old version ID is deleted. With Blob soft
delete enabled, that version remains recoverable until retention expires.
Deleting a previous Blob version through Microsoft Entra ID requires the
`Storage Blob Data Owner` role or a custom role with
`deleteBlobVersion/action`; `Storage Blob Data Contributor` alone cannot do it.
Blobs created before versioning was enabled have no version ID. Their first
refresh succeeds after the operator sets `Copies__VersioningEnabled=true` to
confirm the account setting, but the unaddressable old version is retained and
the target records a cleanup warning. Later refreshes have a captured version
ID and soft-delete that exact previous version.

Compute targets are queued to `collection-work`; the App Service dispatcher
starts a Manual Container Apps Job execution for each target. The worker exits
nonzero on failure and publishes its marker only after artifact uploads.
An independent hosted reconciler checks persisted Table attempts at startup
and every 15 seconds, even when no collection import is active. It requeues
queued attempts whose message may have been lost, checks platform executions,
and verifies worker completion markers. The dispatcher atomically claims a
queued attempt before requesting an Azure execution, so duplicate queue
messages cannot launch duplicate jobs. If the start response or Table update
is lost, the target stays in `launch-uncertain` rather than blindly launching
again. A valid completion marker can resolve it automatically; otherwise an
operator must inspect Container Apps executions before deciding whether to
retry. An App Service restart does not currently resume collection-wide
planning: persisting the import run intent and resuming scheduling is deferred.
Enable **Always On** for the App Service so the queue dispatcher and reconciler
keep running while the dashboard is idle; they still resume from Table state
after a process restart.
The reconciler also checks platform execution status to catch failures the
Python process could not write to Tables. A failed target leaves prior verified
outputs available and blocks dependents for that run. A later Import retries it.
The dashboard stores a collection-wide limit of 1–5 outstanding targets in a
`__settings` row of the `CollectionTargets` table; the default is 1. It counts
copy and compute targets together, including attempts recorded in Table
Storage. The scheduler rereads the limit during an import, so a saved change
affects subsequent scheduling without restarting the app. The limit controls
concurrency, not the scope of an import: `Import collection` can still proceed
through every due target and its descendants.

## Validation sequence

1. Verify-only and apply bootstrap, then run apply again and confirm zero newly
   adopted targets.
2. Before enabling the image targets, confirm the 83 previously completed
   targets still plan as current. The additive graph contains six new target
   kinds and must not change legacy target IDs, recipes, or inputs.
3. Do not use `Import collection` for a bounded single-target canary until a
   single-target action exists. A limit of 1 still permits later descendants
   and other due targets to run sequentially.
4. After a bounded single-target action exists, run one parse-shard canary and
   verify its output marker and Table state without scheduling descendants.
5. Test a single dump-shard replacement separately. Confirm the old current
   Blob remains available until the new copy verifies, then check version
   cleanup and any recorded warning.

The control plane deploys from GitHub to App Service, and the worker image is
published to public GHCR. A Container Apps Job should use an immutable image
digest. Provisioning and live worker verification are separate from this code.

The App Service's `ContainerJob__Image` setting must be changed to the new
image digest after the worker-image workflow publishes it; publishing a GHCR
tag alone does not update a pinned setting.

## Image metadata targets

`source-set` records the pinned article manifest and two dated SQL image dumps.
The new copy targets use the official 2026-10-01 `dumpstatus.json` SHA-1 and
length for `enwiki-20261001-image.sql.gz` and
`commonswiki-20261001-image.sql.gz`. The files are about 88 MB and 18.6 GB
compressed, respectively. Workers stream them into Snappy Parquet without an
uncompressed disk copy. The English Wikipedia conversion retains its whole
image table; the Commons conversion filters to parsed image-reference titles.
The final resolution target gives a local English Wikipedia file precedence
over the same filename on Commons, and records missing or non-image media.
Each metadata row retains the pinned source-dump SHA-1. Filename matching
preserves case after the first character; duplicate normalized titles within
a source are marked ambiguous and excluded from byte totals.

The SQL files contain **original** byte sizes. The gallery can show the known
original-byte total for a branch after resolution, counting each file once.
This is not a 330-pixel thumbnail transfer estimate. Actual thumbnail sizes
can be recorded when thumbnail copying is added. These metadata targets do not
download image bytes.
