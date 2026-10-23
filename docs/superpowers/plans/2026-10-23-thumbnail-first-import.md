# First Thumbnail Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import the 24 thumbnails visible on a preview page at 250px through a resumable control-plane job.

**Architecture:** Azure Tables stores a job snapshot and asset/rendition progress. A hosted service reloads incomplete jobs, resolves thumbnail URLs in small batches, copies one file at a time, and verifies each Blob. The preview starts a bounded job and shows its outcomes.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor, Azure Tables, Azure Blob Storage, Wikimedia imageinfo.

**Spec:** `docs/superpowers/specs/2026-10-23-thumbnail-first-import-design.md`

## Global Constraints

- Only the 24 currently visible images may be queued by one action; requested width is 250px.
- No image becomes a collection graph target.
- One global thumbnail GET per second and one in flight; respect 429 and 503.
- Resume incomplete jobs after App Service restart and skip verified, current renditions.
- Preserve the old Blob until its replacement verifies, then soft-delete the exact old version.

## Review Focus

- A job created just before a restart must resume from its recorded titles.
- A Blob committed just before a restart must not be downloaded twice.
- An imageinfo 429 or thumbnail GET 429 must delay further Wikimedia requests.
- An image title with Unicode, spaces, or punctuation must map to a stable safe Blob path.
- Two jobs containing the same image must not transfer the same current rendition twice.

---

### Task 1: Persist bounded job and rendition state

**Files:** Add `Images/ThumbnailImportState.cs`, `Images/ThumbnailImportRepository.cs`; modify `Program.cs`; add `ThumbnailImportRepositoryTests.cs`.

**Interfaces:** `IThumbnailImportRepository` creates/reads jobs, lists incomplete jobs, and reads/writes asset and width-specific rendition state; `ThumbnailImportJob` snapshots at most 24 titles.

- [ ] Write a failing test for the 24-item cap, stable width-specific key, and reload of an incomplete job.
- [ ] Run the focused test and verify it fails for the missing behavior.
- [ ] Implement Table-backed state using the existing `CollectionTargets` Table, with optimistic ETags where a retry can race.
- [ ] Run the focused test and full .NET suite.

### Task 2: Copy and resume

**Files:** Add `Images/ThumbnailImportService.cs`, `Images/ThumbnailBlobCopier.cs`; modify `WikimediaImageInfo.cs`, `Program.cs`; add `ThumbnailImportServiceTests.cs`.

**Interfaces:** `ThumbnailImportService.StartAsync(titles, 250)` creates a job; hosted execution scans incomplete jobs and processes each title. `ThumbnailBlobCopier.CopyAsync` streams one Wikimedia GET into a stable width-specific Blob.

- [ ] Write failing tests for skip-current, restart resume, 429 backoff, and width-specific destination.
- [ ] Run focused tests and verify expected failures.
- [ ] Implement the copier and hosted service, using returned imageinfo URLs and a global one-per-second request gate.
- [ ] Run focused tests and full .NET suite.

### Task 3: Expose the bounded action and status

**Files:** Modify `Components/Pages/ImagePreview.razor`; add `Components/Pages/ThumbnailImportJob.razor`; modify `Program.cs`; add appropriate component/model tests.

**Interfaces:** The preview starts a job for its 24 visible candidates. A job route shows aggregate progress and each file outcome.

- [ ] Write a failing test for the bounded selection and job status model.
- [ ] Run it and verify expected failure.
- [ ] Implement the button and job view, keeping live preview URLs unchanged.
- [ ] Run the full .NET suite and Release publish.

### Task 4: Bounded live canary

**Files:** Update `docs/COLLECTION_IMPORT_OPERATIONS.md`.

- [ ] Verify Table and Blob permissions and confirm no thumbnail job is already active.
- [ ] Publish the application and start only one 24-image preview-page job.
- [ ] Check job status, Blob bytes and content type, retry behavior, and restart continuation where safely testable.
- [ ] Record observations and any limitations in operations documentation.
