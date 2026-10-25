# Control API and Priority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Suspend default-priority images and provide controller-style HTTP operations for the control app.

**Architecture:** SQL repository enforces a fixed priority floor at queue creation and claiming. Controllers use the existing coordinator and repositories. App startup registers controller routing only for the enabled SQL image store.

**Tech Stack:** ASP.NET Core 10, Azure SQL, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-25-control-api-priority-design.md`

## Global Constraints

- Default priority is zero; priority below one is ineligible for new claims.
- An existing in-flight attempt may complete after a priority change.
- API mutations use existing repository methods and current App Service authentication.

## Review Focus

- A configured minimum of zero still suspends zero-priority images.
- Pre-existing queued zero-priority work cannot be claimed.
- Unknown target IDs cannot create arbitrary target state.
- Invalid image IDs and nonfinite priorities cannot mutate SQL.
- Image API routes are unavailable when SQL image work is disabled.

---

### Task 1: Priority floor

**Files:** `apps/Creational.Dataflow/Images/SqlImageWorkRepository.cs`, `apps/Creational.Dataflow.Tests/SqlImageWorkTests.cs`

- [x] Add a SQL-backed test for priority zero versus one with minimum zero, including pre-queued work.
- [x] Enforce the floor in metadata filling, rendition filling, and claiming.
- [x] Run focused tests.

### Task 2: Controller API

**Files:** `apps/Creational.Dataflow/Api/CollectionsController.cs`, `apps/Creational.Dataflow/Api/ImageWorkController.cs`, `apps/Creational.Dataflow/Program.cs`, API tests.

- [x] Add request/response contracts and collection controller using existing coordinator/repositories.
- [x] Add image-work controller using SQL repository and validate mutation inputs.
- [x] Register and map controllers; smoke test endpoints locally.

### Task 3: Documentation and verification

**Files:** `docs/sql-operations-cutover.md`, spec and plan.

- [x] Document routes and fixed priority floor.
- [x] Run all relevant .NET and Python tests and inspect the diff.
- [ ] Commit, push, and verify web deployment.
