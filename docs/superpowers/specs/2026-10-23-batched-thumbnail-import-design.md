# Batched thumbnail scheduling and job history

## Intent

An operator viewing an OTT subtree or Wikipedia page can schedule more than the 24 visible images and later return to active or past image imports. The action remains bounded while Wikimedia request pacing is being measured.

## Behavior

- The preview offers a count (default 100, maximum 1,000) and an exact starting image, initially the current preview page's first image. The visible-page button remains available.
- A batch record persists the parsed selection, starting offset, requested count, and cursor. The hosted service creates deterministic child jobs of at most 24 titles, processes them serially, and advances the cursor. It can resume planning and copying after restart without duplicating child jobs.
- The image-jobs index lists batches and standalone jobs, newest first. A batch page shows its progress and links to its child jobs. The existing job page links back to the index.
- Batch scheduling only reads the catalog. A scheduled batch can take hours to copy at the current Wikimedia pace; navigating away does not stop it.
- The collection DAG, curation behavior, and rendition identity remain unchanged.

## Approaches considered

Putting up to 1,000 titles into a single Table row would make the job-detail page and row-size limits awkward. Creating all child jobs in the click handler would lose the unscheduled remainder if the web process restarts. A persisted batch cursor plus small child jobs reuses the proven import path and makes the intent durable.

## Limits

This does not yet schedule the entire 240,000-image animal subtree in one action. The operator can schedule successive non-overlapping ranges of up to 1,000; a later bulk mode can raise the limit after measured throughput and Table query costs are known.
