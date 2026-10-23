# Image Import Runs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator import every image in a selected subtree through automatically chained, restart-safe 1,000-image batches.

**Architecture:** Stream one DuckDB catalog query into deterministic Blob manifests. Publish a parent run row only when the snapshot is complete. The existing hosted image importer schedules one child batch, reconciles its terminal counters, and advances the parent cursor. Blazor exposes scheduling, a run list, and a run detail page.

**Tech Stack:** .NET 10, DuckDB.NET, Azure Blob Storage and Tables, Blazor Server, MudBlazor, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-23-image-import-runs-design.md`

## Global Constraints

- Preserve standalone batches and legacy 24-image jobs.
- Keep 1,000 images as the batch limit and 250px as the initial run width.
- Do not start an all-animal run during verification.
- Continue using the existing Wikimedia request pacer and image reuse logic.

## Review Focus

- A last chunk below 1,000 has the correct absolute offsets and count.
- A restart after child insertion does not create another child.
- A restart after a child finishes counts its outcomes once.
- A missing catalog result creates no parent run.
- More than one active run does not make image copies parallel unexpectedly.

---

### Task 1: Stream and snapshot a catalog selection

**Files:** `Images/ImageCatalogQuery.cs`, `Images/ImageCatalog.cs`, `Creational.Dataflow.Tests/ImageCatalogQueryTests.cs`

**Interfaces:** `IImageCatalog.SnapshotAsync(selection, chunkSize, sink, ct)` streams ordered `ImageCandidate[]` chunks to an async sink and returns the total count.

- [ ] Write failing tests for ordered chunks and final short chunk from the DuckDB fixture.
- [ ] Run focused tests to confirm failure.
- [ ] Share the candidate-selection SQL between preview and streaming methods; keep only one chunk in memory.
- [ ] Run focused and full .NET tests.

### Task 2: Persist and reconcile parent runs

**Files:** `Images/ThumbnailImportState.cs`, `Images/ThumbnailImportRepository.cs`, `Images/ThumbnailImportService.cs`, `Creational.Dataflow.Tests/ThumbnailImportTests.cs`

**Interfaces:** `CreateRunAsync(selection, ct)` writes chunk manifests then a run row; `AdvanceRunAsync(run, ct)` idempotently inserts or reconciles the deterministic current child; run repository methods persist and enumerate runs.

- [ ] Write failing tests for multi-chunk snapshot, no-result selection, child insertion/restart, and one-time accounting.
- [ ] Run the tests to confirm failure.
- [ ] Implement run rows, parent IDs on batches, snapshot scheduling, and reconciliation before and after active batch processing.
- [ ] Run focused and full .NET tests.

### Task 3: Expose run scheduling and monitoring

**Files:** `Components/Pages/ImagePreview.razor`, `Components/Pages/ImageJobs.razor`, `Components/Pages/ImageRun.razor`, `docs/IMAGE_PREVIEW_OPERATIONS.md`

**Interfaces:** preview calls `CreateRunAsync`; jobs list links runs; run detail displays cumulative totals and child links.

- [ ] Add the explicit import-all action and run pages, keeping bounded batch actions.
- [ ] Document run semantics and restart behavior.
- [ ] Run `dotnet test`, Release publish, and `git diff --check`.
- [ ] Push and verify the web deployment; perform only a bounded live canary, not the full animal import.
