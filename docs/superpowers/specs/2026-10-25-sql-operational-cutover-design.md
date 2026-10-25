# SQL operational cutover

## Intent

Move the collection and image schedulers from Azure Tables to the populated
Azure SQL catalog. Preserve already completed work, use bounded indexed claims,
and make restarts safe. The existing dashboard may remain a simple historical
view while the worker and control-plane paths are cut over.

The live Carnivora import and final resize sweep have finished. The SQL catalog
already contains 400,545 images and five pending rendition rows per image.
Existing imported Blobs are under `wikimedia-extracts/thumbnails/...`; the new
public `images` container and ID-based paths do not yet exist. The control app
has no SQL connection setting yet. These are deployment prerequisites, not
reasons to redownload completed images.

## Approaches

1. Adapt the existing Table job and batch model to SQL. This minimizes code
   changes but retains repeated catalog scans, frozen large runs, and legacy
   Blob names.
2. Let each Python worker access SQL. This gives workers direct claims, but
   adds a second SQL driver, managed-identity login, and transaction model to
   the container.
3. **Chosen:** SQL owns claims and attempts; the control app processes
   metadata and source copies, and the Python resize worker processes a
   bounded manifest and returns an immutable result marker. The collection
   worker remains stateless. This keeps one SQL implementation and makes
   Python execution restartable through reconciliation.

## Durable state

- `CollectionTargetState` stores the fields of `TargetState`, with a rowversion
  for compare-and-swap. `CollectionTargetAttempt` keeps launch and outcome
  history. `CollectionSetting` stores parallelism. The target graph and recipe
  logic stay in code; the existing Azure Queue may continue to signal compute
  launches. Its dispatcher reads one target by key, never the whole collection.
- `ImageWorkSetting` has three kinds: metadata, source copy, and resize. Each
  has `Enabled`, `MinimumPriority`, concurrency, and pacing. All are disabled
  at deployment until adoption and a bounded canary have been verified.
- `ImageWorkQueue` has at most one row per image and kind. It contains the
  priority copied from `Image`, ready/delayed/leased state, next eligible time,
  lease token/expiry, and an attempt number. Its ready index orders by kind,
  priority descending, then image ID. The scheduler claims with an atomic
  `UPDATE TOP`/`OUTPUT` under row locks, so two control instances cannot take
  the same item. A separate due-time index activates delayed retries.
- `ImageWorkAttempt` records bounded diagnostics. Successful results update
  `Image` or `Rendition` and remove their queue row in one transaction. Failure
  records an error and retry time; unsupported/missing sources are blocked
  until stale or metadata changes. Expired leases can be reclaimed. A
  completed stale rendition keeps its previous `Bytes` until a verified
  replacement succeeds.
- Metadata success stores the full imageinfo snapshot in Blob under a
  deterministic ID path and writes only its broad license category plus
  freshness state to SQL. Copy success and resize success are recorded only
  after the destination Blob, metadata, byte count, and checksum verify.
  `Rendition.Bytes` stays a signed 32-bit value for the supported widths.

The initial queue can be populated from the SQL catalog in bounded batches.
Metadata and copy eligibility respects source status and work-kind settings.
Successful source copies can enqueue one resize item that covers all four
derived widths. A focus change updates `Image.Priority` and matching queue
priorities in batches. Negative values have no special meaning; they are
below the default minimum of zero.

## Existing asset adoption

The adopter streams legacy Table assets and renditions. It maps each requested
title through the recorded source wiki and resolved title to the unique SQL
`Image` key. It does not attach ambiguous or unmatched rows to a guessed ID;
they are counted and quarantined. A duplicate legacy outcome for the same
`(ImageId,ShapeId)` is also quarantined until resolved.

For each matched completed rendition, copy the old private Blob to
`images/creatures/{shape-key}/{imageId}` within the same storage account,
verify size, content type, checksum and source lineage, then mark SQL complete.
Copy the latest imageinfo snapshot to an ID-based Blob path and classify its
license. Keep all old Table rows and Blobs through validation. An interrupted
adoption resumes from SQL checkpoints; a copied but unrecorded Blob is verified
and adopted on retry. No old Blob deletion is part of this cutover.

## Worker and deployment behavior

The control app handles imageinfo in small API batches and source image copies
with configured bounded parallelism. It writes a resize manifest for a small
set of claimed image IDs. The existing Container Apps Job runs the Python
resizer against `images/creatures/w250/{imageId}`, publishes derived WebP Blobs
to their shape paths, and writes a result marker. The control app reconciles
the marker and job status into SQL; a missing marker cannot be success.
Each result is independently verifiable, so retry does not recompute completed
and current renditions. The collection Python worker still reads/writes Blob
manifests, while its scheduler and attempt state move to SQL.

Deploy in phases: add schema; adopt Table state and copy verified assets;
compare counts/bytes and sample licenses; build and publish the worker image;
configure managed identity and SQL connection for the control app; disable old
Table writers and switch its repositories; enable one high-priority canary;
then enable broader work only after observed query, retry and restart behavior
is acceptable on Basic. The current image UI can be read-only during this
stage; it must not create legacy Table jobs after the switch.

## Verification gates

- Collection target and setting rows agree with the old Table state, including
  stale revisions and active-attempt reconciliation.
- Every SQL-complete rendition has a verified ID-path Blob. Old assets remain
  available until after cutover validation. Quarantined rows are counted.
- SQL claims are exclusive under concurrent control instances, respect
  priority/minimum/delay, and recover after lease expiry.
- The Python job's partial success and retry preserve verified old bytes.
- Queue size, index size, candidate-claim latency, and database allocation
  fit the 2 GiB Basic limit. No full rendition scan occurs per item.
- The new scheduler starts disabled and cannot start all 400,545 images merely
  because the app restarts or the UI is refreshed.
