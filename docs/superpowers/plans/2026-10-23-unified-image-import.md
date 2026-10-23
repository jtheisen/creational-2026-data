# Unified Image Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Process scheduled image imports as one durable batch with shared, persisted imageinfo and width-specific thumbnail copies.

**Architecture:** A Blob manifest freezes batch inputs; Azure Tables keeps a per-image cursor and outcomes. The App Service hosted worker resolves missing metadata in ten-title requests, then copies each image in a bounded window. Legacy jobs remain supported.

**Tech Stack:** .NET 10, Blazor Server, Azure Blob and Table Storage, Wikimedia Action API.

**Spec:** `docs/superpowers/specs/2026-10-23-unified-image-import-design.md`

## Global Constraints

- No new Container Apps job or collection DAG target.
- Existing imported renditions and legacy batch history remain valid.
- Use the existing global Wikimedia request pacer and retry throttles.
- Do not persist redundant original or thumbnail CDN URLs.

## Review Focus

- Restart after manifest upload but before Table insertion leaves no active batch.
- Restart after copy but before item/cursor update skips an already-current rendition.
- A 429 does not advance the cursor or mark the item permanently failed.
- A missing file does not prevent later images from progressing.
- Asset metadata updates preserve the curation value and other widths' renditions.

---

### Task 1: Persist batch inputs and item outcomes

**Files:** `Images/ThumbnailImportState.cs`, `Images/ThumbnailImportRepository.cs`, new `Images/ThumbnailBatchManifestStore.cs`, `Program.cs`, `ThumbnailImportTests.cs`.

**Interfaces:** `CreateBatchAsync` snapshots catalog candidates into an immutable Blob manifest. Repository methods add and list batch item rows; batch row records manifest path and per-image cursor.

- [ ] Add tests for immutable input, deterministic item IDs, and restart at an existing item.
- [ ] Run focused tests and observe failures.
- [ ] Implement model, Blob manifest, and Table persistence.
- [ ] Run focused and full tests.

### Task 2: Persist and reuse imageinfo

**Files:** `Images/WikimediaImageInfo.cs`, new `Images/ImageInfoStore.cs`, `Images/ThumbnailImportRepository.cs`, `Images/ImageCatalogQuery.cs`, `Images/ImagePreview.razor`, `WikimediaImageInfoTests.cs`, `ThumbnailImportTests.cs`.

**Interfaces:** `EnsureAsync` checks asset metadata, fetches missing titles in groups of at most ten by source wiki, archives each response, and merges queryable fields into the asset row. The preview reads this same state.

- [ ] Add tests for multi-title requests, reused assets, missing rights, and raw-response persistence.
- [ ] Run focused tests and observe failures.
- [ ] Implement the shared metadata service and preview use.
- [ ] Run focused and full tests.

### Task 3: Run one batch in bounded windows

**Files:** `Images/ThumbnailImportService.cs`, `Images/ThumbnailBlobCopier.cs`, `Images/ThumbnailImportState.cs`, `ThumbnailImportTests.cs`.

**Interfaces:** New batches process manifest windows directly, archive per-item outcomes, and advance the cursor after each terminal image. Legacy child jobs continue through the existing path.

- [ ] Add tests for 25+ images with no child jobs, metadata reuse across widths, retry after throttle, and restart after a copy.
- [ ] Run focused tests and observe failures.
- [ ] Implement new batch runner and deterministic Special:Redirect/file source.
- [ ] Run focused and full tests.

### Task 4: Present and deploy

**Files:** `Components/Pages/ImageBatch.razor`, `docs/IMAGE_PREVIEW_OPERATIONS.md`, `docs/COLLECTION_IMPORT_OPERATIONS.md`.

- [ ] Show new batch counts and paged per-image outcomes while preserving legacy child-job display.
- [ ] Run .NET tests and Release publish.
- [ ] Verify one small live batch, push, and check deployment.
