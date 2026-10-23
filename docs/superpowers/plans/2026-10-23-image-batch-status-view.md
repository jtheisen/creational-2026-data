# Image Batch Status View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show and filter every image in a unified batch while distinguishing imageinfo from thumbnail-copy progress.

**Architecture:** A pure projection combines manifest entries, terminal item rows, and one in-memory worker activity record. The Blazor page loads the manifest once, refreshes outcomes and activity while running, and reads metadata for rendered rows through a small badge component.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor, Azure Tables and Blob Storage.

**Spec:** `docs/superpowers/specs/2026-10-23-image-batch-status-view-design.md`

## Global Constraints

- Preserve legacy child-job batches.
- Keep "skipped" specific to thumbnail copying.
- Do not persist transient worker activity.
- Do not call Wikimedia merely to populate the batch viewer.

## Review Focus

- A terminal skipped row stays skipped even when its offset overlaps an active window.
- A window at nonzero batch start uses absolute offsets.
- A metadata lookup window shows all its unprocessed images as in progress.
- A resumed batch can show pending items without item rows.
- Completed historical batches continue to show all rows after refresh.

---

### Task 1: Project and expose batch progress

**Files:** `Images/ThumbnailBatchView.cs`, `Images/ThumbnailImportService.cs`, `Creational.Dataflow.Tests/ThumbnailBatchViewTests.cs`

**Interfaces:** `ThumbnailBatchView.Build(manifest, startOffset, terminalItems, activity)` returns all rows with copy status; `ThumbnailImportService.GetBatchActivity(batchId)` returns the current in-memory activity.

- [ ] Add failing tests for terminal precedence, nonzero offsets, metadata-window activity, and pending rows.
- [ ] Run the focused tests and confirm failure.
- [ ] Implement the projection and update the worker's activity around metadata and copy operations, clearing it on return or exception.
- [ ] Run focused and full .NET tests.

### Task 2: Add the selector and separate metadata state

**Files:** `Components/Pages/ImageBatch.razor`, `Components/ImageMetadataStatus.razor`, `docs/IMAGE_PREVIEW_OPERATIONS.md`

**Interfaces:** the page loads a manifest once and applies a single-selection MudToggleGroup to the projected rows; the badge reads `IThumbnailImportRepository.GetImageInfoAsync` for visible titles.

- [ ] Add the four toggle values and project all manifest rows, including pending rows.
- [ ] Display copy status and independently display persisted metadata state for visible rows.
- [ ] Keep the legacy child-job branch and document the filter meanings.
- [ ] Run `dotnet test` and `dotnet publish`, then manually check an active and a completed batch.
