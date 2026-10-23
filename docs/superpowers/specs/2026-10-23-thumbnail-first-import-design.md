# First thumbnail import

## Intent

Import a small, reviewable set of Wikimedia thumbnails into Azure Blob Storage from the Blazor control plane. The first run copies at most the 24 images visible on the current preview page. It must make progress after an App Service restart without recopying completed files. Taxon association and curation actions are deferred.

## Scope and behavior

- The existing collection dependency graph still ends at `resolved-image-metadata`. A thumbnail import is a separate operator-created job, not one target per file.
- The preview offers `Import these 24 thumbnails` for the visible page. The job records the exact titles and requested width of 250 pixels, so pagination or source data changing later cannot alter its scope.
- Each image has a durable asset record keyed by its canonical file title. A separate 250px rendition record tracks the current Blob, byte count, checksum, source URL, outcome, and stale state. Missing assets begin with a neutral curation value. No curation UI is part of this slice.
- The Blob path contains `250px` and a stable image key. The original filename remains in the final path component. A later width uses its own rendition row and path.
- The control plane fetches the thumbnail URL via Wikimedia imageinfo, respecting English Wikipedia local-file precedence, then streams a single GET to Blob Storage. It verifies committed bytes before recording success. An existing current rendition is skipped unless stale; a refreshed rendition replaces the Blob and deletes the exact prior version only after verification, relying on Azure soft-delete retention.
- A hosted service resumes incomplete jobs from Azure Tables on startup. Progress is durable per image; transient 429/503 responses back off and retry. The global Wikimedia request pace starts at one thumbnail per second, with one in flight. The UI shows job-level progress and the 24 file outcomes.
- The preview itself continues linking to Wikimedia. Importing thumbnails does not automatically select or reject them.

## Alternatives

- One collection target per image would reuse the existing graph but make the target dashboard unusable at hundreds of thousands of files.
- Only Blob existence plus an in-memory queue would be simpler, but would lose job scope and errors across restarts and make stale marking difficult.
- The chosen job plus per-image rendition records preserves progress while leaving the dependency graph compact.

## Limits

This first slice is deliberately capped to one visible page. Bulk subtree import, cross-taxon curation, and original-size copying follow after observing a small live job and Wikimedia throttling. Multiple simultaneously running App Service instances are not required for this slice.
