# Collection Import Target Graph Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Blazor Server collection-import dashboard that adopts existing verified outputs and selectively runs stale targets through OTT correlation.

**Architecture:** Target definitions and dependencies are code-defined; Azure Tables stores each target's durable stale revision and last verified output pointer. A planner compares those records with Blob manifests, then the App Service copier or Container Apps workers execute only dirty targets. Live copy bytes remain in a process-local ledger.

**Tech Stack:** .NET 10 Blazor Server, Azure.Data.Tables, Azure.Storage.Blobs, Azure.Identity, Azure Storage Queue, Container Apps Jobs, existing Python 3/PyArrow/mwparserfromhell/DuckDB scripts.

**Spec:** [collection-import target-graph design](../specs/2026-10-17-collection-import-target-graph-design.md)

## Global Constraints

- First collection: `enwiki/2026-10-01`; derive its shard count from the pinned manifest, not a hardcoded 19 in graph logic.
- Include OTT 3.3 conversion and correlation; exclude image resolution and downloading from collection execution.
- Preserve verified legacy outputs; never overwrite a generated output group in place.
- A target is a group with a completion manifest containing exact paths, lengths, checksums, and provenance.
- Manual stale state is in Azure Tables; live per-file copy bytes are in memory and may be lost on App Service restart.
- A stale target runs; descendants run only if their content dependencies' verified output fingerprints change.
- `candidate-pool-finalize` gates parsers but its marker fingerprint does not invalidate all parse shards. Each parser also consumes the alias seed directly.
- App Service copies external files through bounded blocks; five is the configurable total ceiling, initially one image source request at a time.
- A verified overwrite soft-deletes the prior version by ID; Azure soft-delete retention performs final removal.
- The workspace is not a Git repository and has no local `dotnet` executable. Run .NET checks where SDK 10 is available (the VM probe used `~/duckdb-net-probe/dotnet/dotnet`, version 10.0.402); add that directory to `PATH` before the plan's `dotnet` commands. Commit steps are deferred until Git exists.
- App Service identity needs Blob and Table data access; the compute launcher needs permission to start its Container Apps Job. Check assignments before any live execution.

## Review Focus

- A 19-year-old current blob replaced under versioning: previous-version cleanup must target the exact old version ID and never the new current version; test in Task 7.
- A stale mark arriving while an attempt runs: the completion must satisfy only the revision captured at start; test in Task 3.
- One pool shard changes while the pool finalizer marker changes: only that shard's parser becomes dirty; test in Task 4.
- A Table row says complete but one group member is missing or its stored length/checksum differs from the manifest: adoption/planning must treat it as missing; test in Task 2.
- App Service restarts during a copy: partial blocks must not replace the current blob, and the next manual import must be able to start again; test in Task 7.

---

## File map

- `apps/Creational.Dataflow/` is the Blazor Server app. `Targets/` owns definitions, manifests, validity, and planning; `Storage/` owns Azure adapters; `Transfers/` owns the in-process copier; `Execution/` owns dispatch and run coordination; `Components/Pages/` owns the UI. `Program.cs` wires services and a one-time adoption command.
- `apps/Creational.Dataflow.Tests/` holds graph, storage-adapter, copier, and component tests. Azure adapters use interfaces so tests need no live account.
- `workers/collection/` contains a thin Python CLI and Dockerfile around existing parser/converter functions. Each invocation receives a target ID, input manifest URI, attempt output prefix, and recipe version; it reports a completion manifest only after verification.
- `tools/upload_existing_results.py` uploads the local parse, OTT, and correlation groups once from a machine with managed-identity Blob access. It never edits the local outputs.
- Existing ad-hoc scripts remain as historical reproductions until cloud workers are validated; avoid changing their pinned 2026 paths to serve as generic entry points.

### Task 1: Scaffold the app and target definitions

**Files:** Create `apps/Creational.Dataflow/Creational.Dataflow.csproj`, `Program.cs`, `Targets/TargetId.cs`, `Targets/TargetDefinition.cs`, `Targets/October2026Collection.cs`, and `apps/Creational.Dataflow.Tests/TargetDefinitionTests.cs`.

**Interfaces:** `TargetId(string Collection, string Kind, string Key)`; `TargetDefinition(TargetId Id, string RecipeVersion, IReadOnlyList<TargetId> ContentDependencies, IReadOnlyList<TargetId> ReadinessGates, IReadOnlyList<string> OutputNames)`; `October2026Collection.Build(IReadOnlyList<ManifestShard> shards) -> IReadOnlyList<TargetDefinition>`.

- [ ] Write tests asserting 19 supplied shards yield exactly one manifest, 19 dumps/indexes/pools/parsers, and one of each finalizer/OTT target; assert a two-shard fixture yields two of each shard target and no cycles.
- [ ] Run `dotnet test apps/Creational.Dataflow.Tests --filter TargetDefinitionTests` and confirm the missing implementation fails.
- [ ] Create the .NET 10 app, test project, domain records, and graph builder with the spec's content edges and readiness gate.
- [ ] Run the same tests and confirm they pass; launch the app and confirm a basic collection list renders.
- [ ] Review file boundaries and record the task checkpoint; commit if Git has been initialized.

### Task 2: Verify output groups and compute stable fingerprints

**Files:** Create `Targets/OutputManifest.cs`, `Targets/TargetVerifier.cs`, `Storage/IBlobStore.cs`, `Storage/AzureBlobStore.cs`, `apps/Creational.Dataflow.Tests/TargetVerifierTests.cs`.

**Interfaces:** `OutputManifest(TargetId, RecipeVersion, IReadOnlyList<ArtifactDescriptor>, IReadOnlyDictionary<string,string> InputFingerprints)`; `TargetVerifier.VerifyAsync(OutputManifest, CancellationToken) -> VerificationResult`; `VerificationResult(bool Valid, string? Fingerprint, string? Error)`.

- [ ] Write tests for a complete two-file group, missing member, mismatched length, mismatched SHA-256, and identical content under two different attempt prefixes producing the same fingerprint.
- [ ] Run `dotnet test ... --filter TargetVerifierTests` and confirm failures are due to missing code.
- [ ] Implement canonical manifest serialization and a Blob adapter that HEAD-checks length and checksum metadata, downloading only when metadata is absent and verification requires it. Fingerprint verified member content plus semantic format, excluding path and marker timestamp.
- [ ] Run the tests and validate the existing pool marker format can be adapted without rewriting its blobs.
- [ ] Review the verifier's failure messages; commit if Git exists.

### Task 3: Persist target state and stale revisions in Azure Tables

**Files:** Create `Targets/TargetState.cs`, `Storage/ITargetRepository.cs`, `Storage/TableTargetRepository.cs`, `apps/Creational.Dataflow.Tests/TargetRepositoryTests.cs`.

**Interfaces:** `ITargetRepository.GetCollectionAsync(collection)`, `MarkStaleAsync(TargetId, reason)`, `BeginAttemptAsync(TargetId, expectedStaleRevision)`, `CompleteAttemptAsync(TargetId, attemptId, capturedStaleRevision, OutputManifest)`, `FailAttemptAsync(TargetId, attemptId, error)`; updates use Table ETags.

- [ ] Write tests for repeated stale increments, conflicting ETags, duplicate attempt completion, and a stale increment during an attempt leaving `staleRevision > satisfiedStaleRevision` after that attempt succeeds.
- [ ] Run `dotnet test ... --filter TargetRepositoryTests` and confirm expected failures.
- [ ] Implement one entity per target with collection PartitionKey, target RowKey, revision fields, completion pointer, last outcome, and bounded error text. Store the manifest in Blob, not Table properties.
- [ ] Run tests; exercise create/read/mark-stale against an isolated test table only when Azure Table credentials are available.
- [ ] Review schema and concurrency behavior; commit if Git exists.

### Task 4: Plan minimal work from graph and verified state

**Files:** Create `Targets/TargetPlanner.cs`, `Targets/ImportPlan.cs`, `apps/Creational.Dataflow.Tests/TargetPlannerTests.cs`.

**Interfaces:** `TargetPlanner.Plan(definitions, states, verifications) -> ImportPlan`; each `PlannedTarget` has `TargetId`, `Disposition` (`Current`, `Run`, `Blocked`, `Failed`), and a reason.

- [ ] Write tests for all-current no-op, one stale parse shard, changed parse output invalidating correlation, unchanged stale dump output preserving downstream, failed pool-finalizer blocking parsers, and changed one pool shard invalidating only its corresponding parser.
- [ ] Run `dotnet test ... --filter TargetPlannerTests` and confirm expected failures.
- [ ] Implement topological planning; compare stored content-dependency fingerprints only after dependencies complete, and use readiness gates solely to block execution.
- [ ] Run tests and inspect a textual 19-shard plan for no accidental whole-stage fanout.
- [ ] Review plan reasons for UI readability; commit if Git exists.

### Task 5: Publish and adopt existing work once

**Files:** Create `tools/upload_existing_results.py`, `tests/test_upload_existing_results.py`, `Targets/ExistingAssetAdopter.cs`, `apps/Creational.Dataflow.Tests/ExistingAssetAdopterTests.cs`; update `Program.cs` with `--adopt-existing --verify-only` and `--adopt-existing --apply` modes.

**Interfaces:** Python CLI reads `results/candidate-parse-v1`, `data/opentree/ott3.3/taxonomy.tsv`, `results/opentree-ott3.3`, and `results/ott-wikipedia-correlation-v3`; .NET adopter verifies Blob manifests then creates current target rows without running transforms.

- [ ] Write Python tests that reject missing local members and mismatched report lengths, and .NET tests that refuse a Blob group with an absent member while adopting a complete group idempotently.
- [ ] Run `python3 -m unittest discover -s tests -p test_upload_existing_results.py` and `dotnet test ... --filter ExistingAssetAdopterTests`; confirm expected failures.
- [ ] Implement a dry-run inventory first. Upload local groups to new versioned prefixes using the VM's managed identity after transferring the local files to the VM; compute missing hashes, verify remote bytes, and write `_COMPLETE.json` last. Adopt the 19 existing dump/pool groups, alias indexes/seed, and newly uploaded groups only after verification.
- [ ] Run dry-run, inspect exact target and byte counts, then run apply; rerun both commands and confirm zero uploads and zero expensive targets. Preserve the old `extract-v2/` history.
- [ ] Save the bootstrap report as a Blob artifact and record the checkpoint; commit if Git exists.

### Task 6: Give Python transforms a target-scoped worker contract

**Files:** Create `workers/collection/__main__.py`, `workers/collection/manifest.py`, `workers/collection/stages.py`, `workers/collection/Dockerfile`, `tests/test_collection_worker.py`; reuse `build_candidate_pool.py`, `parse_candidate_pool.py`, `transform_ott_taxonomy.py`, and `correlate_ott_wikipedia.py` without changing their ad-hoc CLI paths.

**Interfaces:** `python -m workers.collection --target-id ID --inputs-manifest URI --output-prefix URI --recipe-version VERSION`; exit 0 only after verified output manifest publication; exit nonzero with concise stderr on failure.

- [ ] Write fixture tests for alias-index/seed, pool shard/finalizer, parse shard/finalizer, OTT names, and correlation target modes; assert incomplete output never publishes `_COMPLETE.json`.
- [ ] Run `python3 -m unittest discover -s tests -p test_collection_worker.py` and confirm expected failures.
- [ ] Implement thin stage adapters and shared Blob upload/manifest code; each generated group writes to an attempt prefix and checks source fingerprints. Match the adopted recipe versions unless intentionally creating a new output version.
- [ ] Run tests and a one-shard dry run against a small fixture; compare output counts and checksums with the existing scripts where inputs are identical.
- [ ] Review command-line error reporting and scratch cleanup; commit if Git exists.

### Task 7: Stream external sources through the App Service

**Files:** Create `Transfers/TransferLedger.cs`, `Transfers/StreamingBlobCopier.cs`, `Transfers/CopyHostedService.cs`, `apps/Creational.Dataflow.Tests/StreamingBlobCopierTests.cs`.

**Interfaces:** `IStreamingCopier.CopyAsync(CopyTarget, CancellationToken) -> CopyResult`; `TransferLedger.Snapshot(TargetId) -> TransferSnapshot`; `CopyTarget` includes source URL, expected length/hash, destination, and old version ID when replacing.

- [ ] Write tests for bounded block buffering, byte progress, hash mismatch leaving old current Blob intact, 429 backoff, cancellation before commit, restart-like cancellation then manual retry, and deletion of only the captured old version ID after a verified replacement.
- [ ] Run `dotnet test ... --filter StreamingBlobCopierTests` and confirm expected failures.
- [ ] Implement `HttpClient` streaming with `ResponseHeadersRead`, a descriptive Wikimedia User-Agent with real contact information, block staging and final commit, configurable total/source semaphores, and an in-process ledger. Soft-delete the prior version by ID only after successful commit/verification; record cleanup failures.
- [ ] Run tests; measure one dump shard and a small image sample on the actual App Service plan before enabling a full import. Confirm Blob soft delete and versioning are enabled before replacement.
- [ ] Review memory and dashboard latency measurements; commit if Git exists.

### Task 8: Dispatch and reconcile compute targets

**Files:** Create `Execution/ImportCoordinator.cs`, `Execution/IComputeJobLauncher.cs`, `Execution/ContainerAppsJobLauncher.cs`, `Execution/QueueWorkDispatcher.cs`, `apps/Creational.Dataflow.Tests/ImportCoordinatorTests.cs`, and `infra/collection-worker.md` for the manual Container Apps Job and Queue settings.

**Interfaces:** `ImportCoordinator.StartOrGetAsync(collection) -> RunId`; `ImportCoordinator.ReconcileAsync(RunId)` rechecks manifests, Table state, and Container Apps execution status; `QueueWorkDispatcher` claims a queued target and calls `IComputeJobLauncher.StartAsync(TargetId, inputManifestUri, outputPrefix) -> ExecutionId`. An ETag-protected attempt claim makes duplicate queue delivery harmless.

- [ ] Write tests for duplicate Import clicks returning one run, independent shards respecting a configured limit, failed worker blocking dependents, restart reconciliation marking an abandoned attempt incomplete, and successful worker output re-planning descendants.
- [ ] Run `dotnet test ... --filter ImportCoordinatorTests` and confirm expected failures.
- [ ] Implement queue dispatch and manual Container Apps Job invocation with managed identity; persist execution IDs and concise outcomes in Table. The dispatcher deletes a queue message only after a successful start or an already-claimed attempt. Distinguish compute-job recovery from the intentionally non-resumable App Service copy ledger.
- [ ] Run tests and a single fixture Container Apps execution; confirm nonzero exit is visible as failure and successful output is verified before target completion.
- [ ] Review logs and duplicate-delivery behavior; commit if Git exists.

### Task 9: Add the Blazor collection and target views

**Files:** Create `Components/Pages/Collections.razor`, `Components/Pages/CollectionTargets.razor`, `Components/TargetRow.razor`, `apps/Creational.Dataflow.Tests/CollectionPageTests.cs`; update `Program.cs` service registrations.

**Interfaces:** Collection list initially returns the one pinned export; target page consumes `ImportPlan`, Table snapshots, and `TransferLedger` snapshots; actions call `MarkStaleAsync` and `StartOrGetAsync`.

- [ ] Write component tests for one hardcoded collection, grouped target statuses, a stale click affecting one target row, a plan preview showing downstream reasons, and a page refresh reading ongoing transfer state from the singleton ledger.
- [ ] Run `dotnet test ... --filter CollectionPageTests` and confirm expected failures.
- [ ] Implement paged target groups with counts, output-group summaries, last completion/error, Mark stale, and Import collection. Refresh via a disposable timer and `InvokeAsync(StateHasChanged)`; never hold a transfer in a UI event handler.
- [ ] Run component tests and manually verify the page with the adopted 2026 collection; verify WebSockets and Always On in App Service configuration.
- [ ] Review accessibility and error display; commit if Git exists.

### Task 10: End-to-end acceptance and operational handoff

**Files:** Create `docs/COLLECTION_IMPORT_OPERATIONS.md`; update `ARCHITECTURE.md` with implemented behavior and the final target/Blob prefixes.

**Interfaces:** Operator commands for verify-only bootstrap, apply bootstrap, plan-only import, Mark stale, and Import collection; no hidden file-browser operations.

- [ ] Run all Python and .NET tests, and an all-current plan against adopted assets; assert no expensive work is scheduled.
- [ ] Mark one parse target stale and run one collection import; assert one parser runs and correlation runs only if its verified output changes.
- [ ] Mark one dump shard stale and run one collection import; assert one stream copy runs, the other 18 are untouched, and downstream stays current when SHA-256 is unchanged.
- [ ] Interrupt a transfer, verify the old current Blob still serves, and confirm a later manual Import retries safely. Verify prior versions become soft-deleted after successful replacement.
- [ ] Document measured throughput, App Service resource impact, source throttling behavior, Azure permissions, and the remaining image-pipeline boundary. Commit if Git exists.
