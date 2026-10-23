# Batched Thumbnail Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Schedule up to 1,000 selected candidate images beyond the visible page and find current or past image jobs later.

**Architecture:** Azure Tables stores a batch selection and cursor. The hosted importer creates deterministic child jobs of at most 24 titles and resumes a batch after restart. Blazor pages expose batch scheduling, batch progress, and job history.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor, DuckDB, Azure Tables.

**Spec:** `docs/superpowers/specs/2026-10-23-batched-thumbnail-import-design.md`

## Global Constraints

- Default 100, maximum 1,000 images per scheduling action.
- Start at the current preview page; preserve its parsed selection and offset.
- A child job contains at most 24 titles and reuses the existing copier.
- Persist intent before background planning and use deterministic child IDs.
- Keep collection DAG targets and curation unchanged.

## Review Focus

- Restart after child creation but before cursor update must not create a duplicate job.
- A catalog page shorter than requested must finish the batch.
- A batch must not advance to the next chunk before its current child job finishes.
- History must include legacy one-image jobs without batch metadata.
- Two batches for overlapping ranges must skip already current renditions.

---

### Task 1: Durable batch state and scheduler

**Files:** `Images/ThumbnailImportState.cs`, `Images/ThumbnailImportRepository.cs`, `Images/ThumbnailImportService.cs`, `Images/ImageCatalog.cs`, `Program.cs`, `ThumbnailImportTests.cs`.

**Interfaces:** `CreateBatchAsync(ImageSelection selection, int offset, int count)` persists the intent; `ProcessPendingAsync` creates and runs one deterministic child job per batch cursor before advancing it.

- [ ] Add failing tests for count bounds, deterministic child ID, restart replay, and short catalog page.
- [ ] Run focused tests and confirm expected failures.
- [ ] Implement Table batch rows and catalog-backed scheduling.
- [ ] Run focused and full .NET tests.

### Task 2: Job and batch history

**Files:** `Images/ThumbnailImportRepository.cs`, `Components/Pages/ImageJobs.razor`, `Components/Pages/ImageBatch.razor`, `Components/Pages/ThumbnailImportJob.razor`, `Components/Pages/Collections.razor`.

**Interfaces:** List newest batches and standalone jobs; batch details list child jobs and outcome counts; all detail pages link back to `/image-jobs`.

- [ ] Add a failing model test that legacy jobs remain visible beside batched jobs.
- [ ] Run the focused test, implement the index and detail pages, then run full tests and Release publish.

### Task 3: Preview scheduling and rollout

**Files:** `Components/Pages/ImagePreview.razor`, `docs/IMAGE_PREVIEW_OPERATIONS.md`, `docs/COLLECTION_IMPORT_OPERATIONS.md`.

- [ ] Add count and range controls (default 100, maximum 1,000) and show the exact start and end range before scheduling.
- [ ] Verify a small live batch, restart-safe Table rows, and job links.
- [ ] Push and verify the GitHub deployment and App Service logs.
