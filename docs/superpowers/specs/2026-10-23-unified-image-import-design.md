# Unified image import

## Intent

An operator schedules one image import of up to 1,000 catalog candidates. The
control plane retains the exact image list, resolves missing image information
in small multi-title requests, and copies the requested thumbnail for each
image. The batch, rather than a child job inherited from the 24-image preview,
is the unit shown in job history and resumed after a restart.

## Design

- Save an immutable manifest of titles and source wikis in Blob before adding
  the batch Table row. The batch cursor advances by image, and batch-item Table
  rows retain each image's historical outcome for the paged viewer. Shared
  asset and width-specific rendition rows remain independent of job history.
- Work in bounded windows: ensure missing or stale imageinfo records for the
  window in requests of at most ten titles per source wiki, then copy each
  absent or stale rendition. Save metadata and item outcomes before advancing
  the cursor. Recover incomplete work after an App Service restart.
- Persist a raw snapshot of each requested imageinfo page result in Blob and
  queryable metadata on the existing asset row. Include identity, version,
  descriptive and rights metadata; omit original and thumbnail URLs from the
  request. A metadata refresh is independent of a rendition refresh.
- Construct the Wikimedia Special:Redirect/file URL from source wiki, title,
  and requested width. Keep the destination Blob path width-specific. Do not
  require a source CDN URL in the rendition row.
- Keep the legacy 24-image standalone jobs and existing child-job batches
  readable and resumable. New batches use the unified path.
- The preview uses the same persisted imageinfo records. Its display may
  request missing metadata for visible images, but the import never repeats a
  completed lookup solely because of a new width.

## Failure and state rules

An API throttle pauses work without advancing the cursor. A permanent lookup
or copy error records an item failure and advances, so other images proceed.
Missing rights fields are retained as fetched metadata and flagged for review;
they do not themselves block a copy. An imported rendition already current in
Blob is skipped while missing imageinfo is still backfilled. Batch summaries
count copied, skipped, and failed images. Legacy records keep their old meaning.

## Alternatives

Increasing child-job size would reduce row count but retain two overlapping
progress hierarchies and an arbitrary chunk boundary. A single Table row with
all 1,000 titles can exceed a property limit and makes historical image
outcomes awkward. The chosen manifest, batch cursor, and item rows make the
scheduled batch the sole job while keeping the input and outcomes durable.
