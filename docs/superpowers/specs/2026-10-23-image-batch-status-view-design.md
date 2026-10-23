# Image batch status view

## Intent

Show all images in a scheduled batch, including images not yet processed. Let the operator switch between All, In progress, Skipped, and Pending while keeping imageinfo status distinct from thumbnail-copy status.

## Approaches

The existing table contains only terminal image outcomes. A single combined state would make "skipped" ambiguous because metadata can be fetched even when the copy is skipped. Instead, project copy state from the immutable batch manifest, terminal item rows, and the worker's current in-memory activity. Query metadata status separately for visible rows. This keeps the meaning of stored outcomes and avoids up to 1,000 asset reads on each poll.

## Behavior

- All shows every manifest image, including copied and failed outcomes.
- In progress shows the current metadata window while its lookup runs, or the single image being checked/copied afterward. This stage lives only in the running app; a page refresh reads it from the singleton service. After an app restart it appears again when processing resumes.
- Skipped shows terminal images whose thumbnail rendition was already current or recovered. Their imageinfo may have been fetched during this batch.
- Pending shows manifest images without a terminal outcome or active worker stage. Their metadata may already exist, particularly from the preview or a prior batch.
- Each visible row shows its independent persisted imageinfo state: not fetched, stale, ready, missing, or unsupported. Metadata lookups for the viewer must not call Wikimedia.
- Existing child-job batches retain their current historical view.

## Limits

Batch manifests remain capped at 1,000. The job page continues to poll while a batch is active. Filtering is client-side over this bounded manifest, and the table pages at 20 rows. No new Table state is required for transient worker activity.
