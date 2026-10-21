# Image Metadata Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add dated Wikimedia image SQL dumps and derived Parquet metadata to the existing collection pipeline without rerunning the 83 completed targets.

**Architecture:** A pinned source-set target names the two image SQL dumps alongside the existing article source. The App Service copier verifies Wikimedia SHA-1 during transfer and records computed SHA-256. Container workers stream the gzip SQL files, retaining all English Wikipedia rows and only referenced Commons rows, then publish a resolved candidate catalog.

**Tech Stack:** .NET 10, Azure Blob and Table SDKs, Python 3, PyArrow, DuckDB, Wikimedia SQL dumps.

**Spec:** [Image metadata import design](../specs/2026-10-21-image-metadata-import-design.md)

## Global Constraints

- Preserve all 83 existing target identities, recipe versions, dependencies, fingerprints, and Table rows.
- Source files are `enwiki-20261001-image.sql.gz` and `commonswiki-20261001-image.sql.gz`, pinned by the official `dumpstatus.json` SHA-1 and length.
- Do not use `/latest/` as source identity.
- Stream the Commons gzip; do not require an uncompressed local copy.
- Output Parquet uses Snappy and completion markers follow existing verification rules.
- SQL sizes are original bytes; 330-pixel thumbnails are only a later estimated transfer size.

## Review Focus

- MariaDB escaped quotes, backslashes, and non-ASCII names retain the correct filename.
- A truncated gzip or wrong published SHA-1 must fail without completing a target.
- A Commons candidate with an English Wikipedia local collision resolves to the local file.
- A shared image referenced by multiple pages contributes once to a selection total.
- Adding new targets leaves the current 83-target plan unchanged.

---

### Task 1: Source pins and verified copying

**Files:** `Targets/ImageDumpPins.cs`, `Targets/October2026Collection.cs`, `Execution/CollectionImportEngine.cs`, `Transfers/StreamingBlobCopier.cs`, related .NET tests.

**Interfaces:** `ImageDumpPins` exposes the dated URI, length, and SHA-1 for each dump. The copier accepts SHA-1 expectations and always computes SHA-256 for Blob provenance. New copy targets can publish a first completion manifest without a pre-existing adopted marker.

- [ ] Write tests for official pins, SHA-1 mismatch, first-copy publication, and unchanged dispositions for the 83 legacy definitions.
- [ ] Run the focused tests and observe the expected failures.
- [ ] Implement the minimal source-pin and copy path, including a source-set target that cannot dirty legacy targets.
- [ ] Run focused and full .NET tests; check a clean build.
- [ ] Commit.

### Task 2: Streaming SQL to Parquet

**Files:** `workers/collection/image_sql.py`, `workers/collection/stages.py`, `workers/collection/manifest.py`, Python tests.

**Interfaces:** A streaming converter consumes a compressed Blob chunk iterator, validates the declared column order, decodes the needed SQL fields, and writes a Snappy Parquet and count report. Its optional candidate set filters Commons rows; `None` retains every English Wikipedia row.

- [ ] Write tests for escaped SQL values, column order, filtering, truncation, and Parquet contents/codec.
- [ ] Run tests and observe the expected failures.
- [ ] Implement streaming gzip/SQL parsing and two worker stages.
- [ ] Run focused and full Python tests.
- [ ] Commit.

### Task 3: Target graph and worker payloads

**Files:** `Targets/October2026Collection.cs`, `Execution/WorkInputBuilder.cs`, `workers/collection/stages.py`, related tests.

**Interfaces:** New compute targets receive verified dump Blob URIs and the 19 parsed image-reference Parquets where needed. A final stage resolves each candidate to the local English Wikipedia file first, then Commons, writing a query-ready Parquet and coverage report.

- [ ] Write tests for exact graph dependencies, payload member names, local precedence, and missing references.
- [ ] Run tests and observe the expected failures.
- [ ] Implement graph definitions, payloads, and resolution stage.
- [ ] Run focused and full .NET/Python tests, including legacy-plan invariant.
- [ ] Commit.

### Task 4: Adoption, operational validation, and documentation

**Files:** `docs/IMAGE_PREVIEW_OPERATIONS.md`, `docs/COLLECTION_IMPORT_OPERATIONS.md`, tests and verification scripts as needed.

**Interfaces:** The control plane preflight verifies prior targets stay current. New targets follow normal stale/retry rules. Operators can see source dates, copied dumps, and derived metadata groups in the existing dashboard.

- [ ] Add a failing regression test for additive rollout against recorded target state.
- [ ] Implement any needed preflight and display changes.
- [ ] Run all tests and Release publish; inspect Parquet metadata and sample API comparisons.
- [ ] Document expected first-run plan and runtime/storage caveats.
- [ ] Commit.
