# Collection import operations

The dataflow app lives in `apps/Creational.Dataflow`. It is a .NET 10 Blazor
Server control plane for the pinned `enwiki/2026-10-01` collection. The app
lists target groups, explains why each is current or would run, lets an operator
mark a group stale, and starts the import. The collection graph ends with OTT
3.3 conversion and the investigative OTT–Wikipedia correlation. Image previews
and downloads remain a later pipeline.

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
and SHA-256 match. The commit uses the captured current ETag; after the new
current Blob verifies, the exact old version ID is deleted. With Blob soft
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
The coordinator also checks platform execution status to catch failures the
Python process could not write to Tables. A failed target leaves prior verified
outputs available and blocks dependents for that run. A later Import retries it.

## Validation sequence

1. Verify-only and apply bootstrap, then run apply again and confirm zero newly
   adopted targets.
2. Open `/collections/enwiki-2026-10-01`; confirm all 83 targets are current.
3. Mark one parse shard stale. Import; confirm one parser executes. Correlation
   runs only if the verified parse output fingerprint changes.
4. Mark one dump shard stale. Import; confirm only that shard recopies. If its
   SHA-256 is unchanged, candidate-pool and parser descendants stay current.
5. Interrupt a test transfer. Confirm the old current Blob still serves, then
   rerun Import. After successful replacement, confirm the old version is soft
   deleted and the new current Blob remains available.

The current workspace has no Git metadata and no local .NET SDK. .NET tests
were run with SDK 10.0.402 on `wikipedia-dev`; Python tests can run in the
local `.venv`. The Container Apps image and App Service have not been deployed
until resource IDs, registry, and required role assignments are available.
The selected image registry is public GHCR, so Azure Container Registry is not
required. The Container Apps Job should use an immutable image digest.
