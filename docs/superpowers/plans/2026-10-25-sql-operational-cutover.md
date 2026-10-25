# SQL Operational Cutover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run collection and image work from durable SQL state, preserving completed image assets and avoiding catalog-wide scheduling scans.

**Architecture:** A SQL repository owns collection target state and a compact image work queue. The control app performs metadata and source copies; a stateless Python resize job receives bounded Blob manifests and emits results for SQL reconciliation. Adoption verifies legacy Blobs before setting SQL completion flags.

**Tech Stack:** .NET 10, Microsoft.Data.SqlClient, Azure SQL Basic, Azure Blob/Table SDKs, Container Apps Jobs, Python/Pillow, xUnit.

**Spec:** [SQL operational cutover design](../specs/2026-10-25-sql-operational-cutover-design.md).

## Global Constraints

- Never turn on an all-catalog image work kind during migration or on app startup.
- Preserve old Table rows and thumbnail Blobs until SQL adoption and canary verification are complete.
- Use `images/creatures/w250/{ImageId}` and corresponding `w20`, `w40`, `w60`, `w120` paths; verify before marking complete.
- Keep SQL text `VARCHAR` under UTF-8 collations, and stay below Basic's 2 GiB allocated file limit.
- Image IDs are global and source wiki plus canonical file title is their unique external key.
- `Rendition` stores byte count and three flags; checksums and recipes remain in Blob metadata.

## Review Focus

- Two schedulers claiming the same ready item must produce one lease and one attempt.
- A lost worker launch response must not silently start a duplicate execution.
- A requested title that resolves to a different or ambiguous canonical title must not adopt the wrong Blob.
- A verified old Blob must remain complete and retain its byte count if a replacement fails.
- Disabling a work kind or raising minimum priority must stop new claims without erasing completed work.

---

### Task 1: Collection state in SQL

**Files:** `Sql/Migrations/006_operational_state.sql`, `Sql/SqlTargetRepository.cs`, `Sql/SqlCollectionSettingsRepository.cs`, `Storage/ITargetRepository.cs`, `Execution/QueueWorkDispatcher.cs`, integration tests.

**Interfaces:** `SqlTargetRepository : ITargetRepository`; add `GetTargetAsync(TargetId,ct)` for point lookup. Implement compare-and-swap via rowversion, audit attempts, and bounded retries. Add an idempotent Table-to-SQL adopter with a verify-only mode. Preserve the existing queue and stateless collection worker.

- [ ] Write SQL integration tests for every target transition, stale revision race, and two concurrent launch claims; observe initial failure.
- [ ] Add the migration and repository; make the dispatcher read one target by key.
- [ ] Run focused and whole .NET suites, benchmark point read and active-attempt query on Basic, and adopt/compare live Table target/settings state.
- [ ] Commit.

### Task 2: Image work schema and indexed claims

**Files:** `Sql/Migrations/007_image_work.sql`, `Images/SqlImageWorkRepository.cs`, `Images/SqlImageWorkScheduler.cs`, tests.

**Interfaces:** `ImageWorkSetting` for metadata/copy/resize, `ImageWorkQueue` keyed `(ImageId,Kind)`, `ImageWorkAttempt`, atomic claim/release/complete/fail operations. Ready claims use an index ordered by kind, priority descending, image ID; delayed retries use a due-time index. Materialize only eligible stage work in bounded batches and update queue priorities when image focus changes.

- [ ] Write integration tests for exclusive claim, minimum priority, disabled kind, retry delay, lease expiry, stale completion, and unsupported-source blocking; observe initial failure.
- [ ] Implement migration and repository with transactions and parameterized SQL; use `SqlBulkCopy` for large priority/focus changes.
- [ ] Benchmark queue reservation and claim latency with representative rows on Basic; adjust indexes before live queue population.
- [ ] Commit.

### Task 3: Adopt current image metadata and Blobs

**Files:** `Images/SqlImageAdopter.cs`, `Images/SqlImageIdentity.cs`, `Images/SqlImageBlobStore.cs`, migration progress/quarantine tables, tests, CLI.

**Interfaces:** Stream Table assets and renditions, map by `(SourceWiki,ResolvedTitle)`, quarantine unmatched/duplicate identities, copy and verify legacy Blobs to ID paths, save imageinfo snapshot by ID, classify broad license, and update SQL only after verification. Persist a keyset checkpoint and make reruns idempotent.

- [ ] Write mapping and interrupted-copy tests, including redirects, underscores, duplicate legacy rows, and missing checksum; observe initial failure.
- [ ] Implement verify-only and apply commands; create the public `images` container; run a bounded live canary before the full adoption.
- [ ] Compare counts, byte totals, sampled license categories, Blob checksums, and Basic file growth. Keep old assets.
- [ ] Commit.

### Task 4: SQL-backed metadata and source copy loop

**Files:** `Images/SqlImageWorkScheduler.cs`, `Images/SqlImageInfoProcessor.cs`, `Images/SqlSourceCopyProcessor.cs`, `Program.cs`, tests.

**Interfaces:** Control-plane hosted service claims small SQL batches with configurable concurrency and pacing, reuses ID-path snapshots, refreshes stale metadata, copies to `images/creatures/w250/{ImageId}`, verifies before SQL completion, records attempts and retries. Disable old Table image writers when SQL mode is enabled; leave old UI read-only until replacement controls exist.

- [ ] Write tests for restart/lease recovery, Wikimedia throttling, unsupported media, priority order, disabled startup, and verified replacement failure; observe initial failure.
- [ ] Implement hosted service and DI/configuration wiring. Keep defaults disabled.
- [ ] Run suite and a one-image live canary; verify SQL attempt, Blob, and no legacy Table write.
- [ ] Commit.

### Task 5: Manifest-based resize worker and reconciliation

**Files:** `workers/resize/*.py`, `Images/SqlResizeBatchDispatcher.cs`, `Execution/ContainerAppsJobLauncher.cs`, Python and .NET tests.

**Interfaces:** Claim a bounded set of source-complete images, write a manifest with IDs and expected source checksums, launch a Container Apps Job, publish per-shape results and an immutable completion marker. Reconcile verified results into SQL and release failed/expired claims. Preserve complete old renditions on failure.

- [ ] Write Python tests for partial success, corrupt source, current output skip, and restart; .NET tests for duplicate launch and reconciliation; observe initial failure.
- [ ] Implement worker and control-plane dispatcher/reconciler; publish a new GHCR image and pin the job launcher to its digest.
- [ ] Run tests and a small live resize canary; inspect execution, marker and SQL rows.
- [ ] Commit.

### Task 6: Deployment and end-to-end verification

**Files:** deployment settings, migration report, minimal UI guards if needed.

- [ ] Configure the App Service SQL connection and managed identity, keep image kinds disabled, and deploy the branch.
- [ ] Verify collection state, adopted image totals, priority/claim timings, lease recovery, and a bounded metadata-copy-resize flow on Basic.
- [ ] Record SQL sizes, queue depth, unresolved adoption rows, Blob totals, and remaining capacity. Preserve old Table/Blob assets.
- [ ] Commit report and push the branch.
