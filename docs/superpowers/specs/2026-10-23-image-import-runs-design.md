# Image import runs

## Intent

An operator can import every candidate image in an OTT branch or Wikipedia page selection, including selections larger than 1,000. One run represents the whole request and advances automatically through bounded thumbnail batches. Existing images and imageinfo remain reusable.

## Approaches

Raising the batch limit would create an unwieldy single job and an enormous result page. Requerying the catalog at offsets for each batch would repeatedly rebuild the subtree selection and could change the selection mid-run. The chosen approach scans the catalog once, freezes ordered 1,000-image batch manifests in Blob, and publishes one parent run in Table only after every manifest is written. The parent starts one child batch at a time.

## State and recovery

- A run records its selection, width 250, total image count, batch size 1,000, next batch index, cumulative copied/skipped/failed counts, and status in Azure Tables.
- Chunk IDs and manifest paths derive deterministically from the run ID and index. A batch row records its parent run ID. The scheduler inserts a missing child batch idempotently, waits for terminal status, adds that batch's counts once, and advances the run cursor. A process restart repeats this safely.
- Child batches use the existing imageinfo and thumbnail copy path. A failed image does not stop later images; the parent finishes `complete-with-errors` if any image failed. Transient worker or storage errors leave the active batch resumable.
- A failed or interrupted snapshot before the parent row is published may leave unreferenced manifest Blobs; it does not publish a partial run. The operator can retry scheduling. Garbage collection of such Blobs is outside this first version.
- The control plane handles one active child batch per run. Its existing global worker loop processes active batches sequentially, so creating multiple runs does not multiply Wikimedia request concurrency.

## UI

- The image preview offers an explicit “Import all candidates” action alongside the existing bounded batch action. It navigates to the new run page after snapshotting finishes. It does not start a large import automatically.
- The image jobs page lists runs separately from standalone batches and individual legacy jobs.
- A run page shows selection, overall processed count and copied/skipped/failed totals, current status, and a paged list of its batches with links to the existing batch viewer.
- No pause/cancel control is added in this version. A run continues automatically until all batches finish.

## Validation

Test single-scan chunking, nonzero/final chunk sizes, idempotent child scheduling, accounting after restart, and legacy batches remaining independent. Deploy without starting a full animal import; use a small bounded canary for live state if needed.
