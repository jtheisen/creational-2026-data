# Derived Thumbnails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Derive four smaller WebP widths from all current and future 250px thumbnails with durable progress.

**Architecture:** Extend the existing Python worker image with Pillow and a resize command. A Blazor control-plane service persists sweep state in Azure Tables, starts/reconciles the existing Container Apps Job, and shows progress. The worker persists per-rendition lineage and skips verified current output.

**Tech Stack:** Python 3.12, Pillow, Azure Blob and Table SDKs, .NET 10 Blazor Server, Azure Container Apps Jobs.

**Spec:** `docs/superpowers/specs/2026-10-24-derived-thumbnails-design.md`

## Global Constraints

- Widths are 20, 40, 60, and 120px; 250px sources remain unchanged.
- WebP output is static, uncropped, no-upscale, orientation-correct, and transparent where needed.
- Use the current GHCR worker image and Container Apps Job; no new standing service.
- Azure Table rendition state must include source checksum and recipe version.

## Review Focus

- A source Blob that differs from its Table checksum must not produce trusted output.
- A failed image must not stop later images; a failed execution must remain observable.
- A restart or timeout must not duplicate completed uploads or lose cursor progress.
- Replacing output must retain the previous version until the new version verifies.
- New 250px imports during an active sweep must cause a subsequent pass.

---

### Task 1: Worker resize and per-rendition state

**Files:** Create `workers/resize/__main__.py`, `workers/resize/core.py`, `tests/test_resize_worker.py`; modify worker requirements and Dockerfile.

**Interfaces:** `run_sweep(run_id, store, deadline_seconds)` reads source rows and writes four width rows; `resize_webp(bytes, width)` returns encoded bytes.

- [ ] Write tests for orientation, transparency, no crop/no upscale, current-output skip, source mismatch, and one-image failure isolation.
- [ ] Run tests and observe expected missing-feature failures.
- [ ] Implement image transform, Azure adapter, lineage checks, verified Blob writes, checkpointing, and bounded execution time.
- [ ] Run worker tests and existing Python suite.

### Task 2: Control-plane orchestration and UI

**Files:** Create `apps/Creational.Dataflow/Images/ResizeSweepService.cs`, `apps/Creational.Dataflow/Images/ResizeSweepRepository.cs`; modify launcher, Program, ImageJobs, and import service; add focused .NET tests.

**Interfaces:** `StartResizeAsync(string runId)` starts the resize command; repository stores active run, cursor, counters, execution and retry state.

- [ ] Write tests for resize launch command, single logical run, continuation, failure visibility, and import-triggered resweep.
- [ ] Run tests and observe expected missing-feature failures.
- [ ] Implement durable orchestration and dashboard trigger/status.
- [ ] Run targeted and full .NET test suites.

### Task 3: Deployment and verification

**Files:** Update `infra/collection-worker.md` and image operations docs.

- [ ] Verify worker managed identity Blob and Table permissions and grant missing least-privilege data roles if authorized.
- [ ] Build and test image locally where possible; run full repository checks.
- [ ] Push the code and image workflow, then update the pinned job image and control-plane setting.
- [ ] Run a bounded live canary, verify Blob/Table state and dashboard progress, then leave full sweep for an explicit operator action.
