# Derived thumbnails

The image catalog already stores copied 250px Wikimedia thumbnails. Produce 20,
40, 60, and 120px static WebP renditions from those local Blobs without new
Wikimedia requests. Each width has its own Blob and Azure Table rendition row.
Use the same filename hash directory as the 250px source, with a `.webp` suffix.
The image is scaled to the requested width, never cropped or enlarged. Apply
orientation, retain transparency, and use the first frame of animated sources.

An operator starts one resize sweep from the Image jobs page. It covers all
complete, non-stale 250px renditions, not a manually selected range. The control
plane starts the existing Container Apps Job with a resize command. The worker
uses its managed identity to read and write Blobs and Table rows. It pages over
source rows, checkpoints its cursor and counters, and exits before the four-hour
execution timeout. The control plane launches the next execution until the
sweep completes. A failed source image records a per-image error while the sweep
continues; infrastructure failures leave the run failed for inspection/retry.

Generated rendition rows record source SHA-256 and recipe version alongside
their own Blob SHA-256 and size. A current target is skipped only when its row
and Blob match the source and recipe. Output is uploaded and verified before
the previous Blob version is soft-deleted. Completed 250px import batches
request a sweep so future imports receive derived sizes automatically.

Progress and errors are visible on Image jobs. No per-image target appears in
the collection dependency graph.
