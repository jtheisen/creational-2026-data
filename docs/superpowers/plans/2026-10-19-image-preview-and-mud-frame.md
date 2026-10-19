# Image Preview and MudBlazor Frame Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an OTT/Wikipedia image preview and make the existing dashboard use the MudBlazor frame and row selection.

**Architecture:** A lazy local Parquet cache is keyed by current target fingerprints and queried by DuckDB.NET. A separate Wikimedia imageinfo client enriches only visible rows. The preview page combines both while shared MudBlazor layout replaces bespoke site chrome.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.11, DuckDB.NET 1.5.6.1, Azure Blob/Table SDKs.

**Spec:** [Image preview and MudBlazor frame](../specs/2026-10-19-image-preview-and-mud-frame-design.md)

## Global Constraints

- No image originals are copied in this release.
- Preserve cross-group target selection and current import actions.
- Wikimedia metadata is requested only for the visible preview page.
- Keep the pinned collection and existing managed-identity storage access.

## Review Focus

- A clicked target row with a link or button must not accidentally mark the target selected.
- An OTT ID absent from the retained taxonomy must show an empty/error state, not all images.
- Repeated file references must count one candidate file while preserving source context.
- Missing Commons files must fall back to English Wikipedia imageinfo.
- Missing license metadata must be shown as requiring review.

---

### Task 1: Selection and MudBlazor shell

**Files:** `Components/App.razor`, `Components/Pages/Collections.razor`, `Components/Pages/CollectionTargets.razor`, `wwwroot/site.css`, `Components/TargetSelection.cs`, selection tests.

- [x] Add a test that toggling a row updates the same ID-based selection used by stale actions.
- [x] Run the test red, implement row clicks with interactive-cell propagation stopped, run green.
- [x] Replace custom header/actions/summaries with MudBlazor layout and components; remove obsolete custom CSS.
- [x] Build and check nested routes plus static assets.

### Task 2: Local catalog and selection query

**Files:** `Images/ImageCatalog.cs`, `Images/ImageSelection.cs`, `Program.cs`, catalog tests.

- [x] Add tests for URL/OTT input parsing and a tiny Parquet catalog covering subtree, duplicate files, and missing matches.
- [x] Run red, then implement lazy artifact download, fingerprint cache, DuckDB query, counts, and paging.
- [x] Run focused tests and confirm no remote Parquet scan is used in the query path.

### Task 3: Wikimedia metadata and preview page

**Files:** `Images/WikimediaImageInfo.cs`, `Components/Pages/ImagePreview.razor`, metadata tests, `Program.cs`.

- [x] Add tests for Commons result, Wikipedia fallback, and missing license metadata.
- [x] Run red, implement sequential visible-page API enrichment and short-term cache.
- [x] Add the paginated MudBlazor gallery and collection navigation; verify no original-copy action is exposed.

### Task 4: Verification and delivery

- [x] Run all .NET tests, Release publish, and diff checks.
- [x] Smoke-test rendered routes if Azure-backed startup and network allow it.
- [ ] Commit and push; inspect the deployment workflow result.
