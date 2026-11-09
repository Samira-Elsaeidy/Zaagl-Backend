# Phase 1 status

| | |
|---|---|
| Current stage | **Stage 1 groundwork complete (PD-51), committed and pushed on `phase1/stage-1` (user approved 2026-11-09).** PD-58 (key-revocation retry) done; final run: build 0 errors, Coordinator 9/9, Links 555/555, API 555/555, frontend 84/84. |
| Scope | PD-50/PD-51: Stage 1 finished with its important groundwork only; testing tasks deferred; **PD-59: Stages 2 to 7 approved, built one stage at a time; Stage 2 IN PROGRESS** (lead checks in between stages). |
| Last verified run | 2026-11-09 on the Stage 1 tree (committed unchanged): backend build 0 errors; tests Coordinator 9/9, Links 550/550, API (QA) 555/555; frontend tsc and lint clean, 72/72 tests (84/84 after the later Arabic/formatBytes fix). |
| Last commit | Zaagl-Backend `9923526` (plan docs only) on `phase1/stage-1`; Zaagl-Frontend `985948d` (user's own docs commit). Stage 1 code: the "Phase 1 Stage 1 groundwork" commit on `phase1/stage-1` in both repos (see `git log`). |
| Final review | docs/phase1-implementation/reviews/stage1-final.md (Head): 38 groundwork items, 33 DONE, 5 PARTIAL, 0 NOT DONE; the one pre-commit REWORK (migration validation) is fixed. |
| Updated | 2026-11-09 |

Status values: NOT_STARTED, IN_PROGRESS, BLOCKED, IMPLEMENTED, TESTING, VERIFIED, FAILED.

## Stage 1 summary

| Result | Count | Tasks |
|---|---|---|
| VERIFIED (independent QA tests) | 3 | BE-001, BE-030, BE-031 |
| IMPLEMENTED (Head accepted, tests green) | 32 | see table below |
| PARTIAL (IN_PROGRESS) | 4 | FE-001, FE-004, OPS-003, QA-010 |
| Deferred testing (PD-51) | 11 | QA-001, 002, 003, 004, 007, 013, 014, 015, 039, ARC-017, BE-027 |
| Not in the groundwork list | 2 | ARC-013 (rate limiting v2 design), and the QA tasks above |

Extra work done in Stage 1 beyond the original task list (all approved decisions): PD-36 (bad credentials → 401), PD-39/42/43/47/48/49 (410 pages, fallback, click limit, HEAD and bots), PD-41 (one-time sign-in code, hub tickets), PD-44 (fallback URL checks), AZ-01 (admin enrol-only token), admin social sign-in refused, cache resilience when Valkey is down, refresh_raced → 409, PD-58 (key-revocation retry with Billing outbox, migration PendingPlanChanges).

## Known issues and follow-ups (before Stage 2)

- Flaky test: EmailJobsTests.Queued_confirm_email_is_sent_with_the_fragment_url fails about 1 run in 20 on the shared test database (test isolation deferred, PD-24/PD-51).
- next dev returned 500 on [slug] routes once; not reproduced since.
- CSP reporting endpoint /bff/csp-report required before production enforcement (ADR-0023).
- Open product questions: what a retried share returns (ARC-002 Q1); content in the main link list (ARC-002 Q2); PD-08 content vs Free monthly cap; storage at takedown and on reinstate (ARC-006 Q2/Q3); resources:write scope; CLI sign-in on servers (U-7); staging hostnames (U-10); www redirect (U-11); accounts and purchases (U-12); Arabic digits kept (U-9); Cloudflare terms (PD-52, before launch).
- Local dev DB: migrations applied through ValidateLinkTargetKindChecks, Resources Initial, Files tasks, Identity one-time codes; the allocator was reset on 2026-11-09 with the user's approval (position 1,000,000, dev key fingerprint).

## Stages 2 to 7

ON HOLD (PD-50/PD-51). Their task rows below keep their planning status.

## Task status

### Stage 1: Architecture, contracts and foundation

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| ARC-001 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-002 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-004 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-005 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-006 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-008 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-014 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-016 | architect | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-001 | backend | P0 | VERIFIED | — | QA suite tests/Zaagl.Api.Tests 555/555; Head ACCEPT (reviews/stage1-backend-fixes.md) | Stage 1 commit (phase1/stage-1) |
| BE-002 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-003 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-004 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-005 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-006 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-007 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-013 | backend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| BE-030 | backend | P0 | VERIFIED | — | QA suite tests/Zaagl.Api.Tests 555/555; Head ACCEPT (reviews/stage1-backend-fixes.md) | Stage 1 commit (phase1/stage-1) |
| BE-031 | backend | P0 | VERIFIED | — | QA suite tests/Zaagl.Api.Tests 555/555; Head ACCEPT (reviews/stage1-backend-fixes.md) | Stage 1 commit (phase1/stage-1) |
| FE-001 | frontend | P0 | IN_PROGRESS | — | PARTIAL: viewer route group, viewer layout, nav entries for /files and /paste/new missing | Stage 1 commit (phase1/stage-1) |
| FE-002 | frontend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| FE-003 | frontend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| FE-004 | frontend | P0 | IN_PROGRESS | — | PARTIAL: lib/server-api.ts not done | Stage 1 commit (phase1/stage-1) |
| FE-005 | frontend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| FE-006 | frontend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| FE-029 | frontend | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| OPS-001 | devops | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| OPS-002 | devops | P0 | BLOCKED | PD-11 | — | — |
| OPS-003 | devops | P0 | IN_PROGRESS | — | PARTIAL: base images not pinned by digest (no registry access) | Stage 1 commit (phase1/stage-1) |
| OPS-004 | devops | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| OPS-005 | devops | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| OPS-006 | devops | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| OPS-014 | devops | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-001 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-002 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-003 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-004 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-006 | qa | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-007 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-008 | qa | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-009 | qa | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-010 | qa | P0 | IN_PROGRESS | — | PARTIAL: authorization matrix covers part of the routes (testing deferred, PD-51) | Stage 1 commit (phase1/stage-1) |
| QA-011 | qa | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-013 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-015 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-039 | qa | P0 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| QA-040 | qa | P0 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-012 | architect | P1 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| ARC-013 | architect | P1 | NOT_STARTED | not in PD-51 groundwork list | — | Stage 1 commit (phase1/stage-1) |
| BE-027 | backend | P1 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| FE-027 | frontend | P1 | IMPLEMENTED | — | Head final review reviews/stage1-final.md; full run 2026-11-09: build 0 errors, 1,186 tests passed | Stage 1 commit (phase1/stage-1) |
| QA-014 | qa | P1 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |
| ARC-017 | architect | P2 | BLOCKED | PD-51 (testing deferred) | — | Stage 1 commit (phase1/stage-1) |

### Stage 2: Code and text sharing (E1.1, CLI core)

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| ARC-003 | architect | P0 | NOT_STARTED | — | — | — |
| BE-008 | backend | P0 | NOT_STARTED | — | — | — |
| BE-010 | backend | P0 | NOT_STARTED | — | — | — |
| CLI-001 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-002 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-003 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-004 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-005 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-006 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-007 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-008 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| FE-007 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-008 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-009 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-010 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-012 | frontend | P0 | NOT_STARTED | — | — | — |
| QA-016 | qa | P0 | NOT_STARTED | — | — | — |
| QA-017 | qa | P0 | NOT_STARTED | — | — | — |
| QA-018 | qa | P0 | NOT_STARTED | — | — | — |
| QA-038 | qa | P0 | NOT_STARTED | — | — | — |
| ARC-010 | architect | P1 | BLOCKED | PD-09 | — | — |
| BE-009 | backend | P1 | NOT_STARTED | — | — | — |
| BE-024 | backend | P1 | NOT_STARTED | — | — | — |
| BE-026 | backend | P1 | NOT_STARTED | — | — | — |
| CLI-010 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| FE-011 | frontend | P1 | NOT_STARTED | — | — | — |
| FE-019 | frontend | P1 | NOT_STARTED | — | — | — |
| FE-021 | frontend | P1 | NOT_STARTED | — | — | — |
| QA-019 | qa | P1 | NOT_STARTED | — | — | — |
| QA-020 | qa | P1 | NOT_STARTED | — | — | — |
| BE-028 | backend | P2 | NOT_STARTED | — | — | — |

### Stage 3: Image and media sharing (E1.2, upload pipeline)

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| ARC-009 | architect | P0 | NOT_STARTED | — | — | — |
| BE-011 | backend | P0 | NOT_STARTED | — | — | — |
| BE-012 | backend | P0 | NOT_STARTED | — | — | — |
| BE-014 | backend | P0 | NOT_STARTED | — | — | — |
| BE-017 | backend | P0 | NOT_STARTED | — | — | — |
| FE-013 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-014 | frontend | P0 | NOT_STARTED | — | — | — |
| FE-015 | frontend | P0 | NOT_STARTED | — | — | — |
| OPS-007 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-008 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-009 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-010 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-011 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-012 | devops | P0 | NOT_STARTED | — | — | — |
| QA-021 | qa | P0 | NOT_STARTED | — | — | — |
| QA-022 | qa | P0 | NOT_STARTED | — | — | — |
| QA-023 | qa | P0 | NOT_STARTED | — | — | — |
| QA-025 | qa | P0 | NOT_STARTED | — | — | — |
| QA-026 | qa | P0 | NOT_STARTED | — | — | — |
| QA-027 | qa | P0 | NOT_STARTED | — | — | — |
| QA-028 | qa | P0 | NOT_STARTED | — | — | — |
| QA-029 | qa | P0 | NOT_STARTED | — | — | — |
| ARC-011 | architect | P1 | BLOCKED | PD-13 | — | — |
| BE-015 | backend | P1 | NOT_STARTED | — | — | — |
| BE-016 | backend | P1 | NOT_STARTED | — | — | — |
| FE-016 | frontend | P1 | NOT_STARTED | — | — | — |
| QA-005 | qa | P1 | NOT_STARTED | — | — | — |
| QA-024 | qa | P1 | NOT_STARTED | — | — | — |
| FE-024 | frontend | P2 | NOT_STARTED | — | — | — |

### Stage 4: Ephemeral file transfer (E1.3, lifecycle, abuse)

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| BE-018 | backend | P0 | NOT_STARTED | — | — | — |
| BE-019 | backend | P0 | NOT_STARTED | — | — | — |
| FE-017 | frontend | P0 | NOT_STARTED | — | — | — |
| QA-030 | qa | P0 | NOT_STARTED | — | — | — |
| QA-031 | qa | P0 | NOT_STARTED | — | — | — |
| QA-032 | qa | P0 | NOT_STARTED | — | — | — |
| QA-033 | qa | P0 | NOT_STARTED | — | — | — |
| QA-035 | qa | P0 | NOT_STARTED | — | — | — |
| QA-047 | qa | P0 | NOT_STARTED | — | — | — |
| ARC-007 | architect | P1 | NOT_STARTED | — | — | — |
| ARC-015 | architect | P1 | NOT_STARTED | — | — | — |
| BE-020 | backend | P1 | NOT_STARTED | — | — | — |
| BE-021 | backend | P1 | NOT_STARTED | — | — | — |
| BE-022 | backend | P1 | NOT_STARTED | — | — | — |
| BE-023 | backend | P1 | NOT_STARTED | — | — | — |
| FE-018 | frontend | P1 | NOT_STARTED | — | — | — |
| FE-020 | frontend | P1 | NOT_STARTED | — | — | — |
| FE-025 | frontend | P1 | NOT_STARTED | — | — | — |
| OPS-013 | devops | P1 | NOT_STARTED | — | — | — |
| QA-012 | qa | P1 | NOT_STARTED | — | — | — |
| QA-041 | qa | P1 | NOT_STARTED | — | — | — |
| QA-042 | qa | P1 | NOT_STARTED | — | — | — |
| QA-048 | qa | P1 | BLOCKED | PD-13 | — | — |
| BE-025 | backend | P2 | NOT_STARTED | — | — | — |
| OPS-030 | devops | P2 | NOT_STARTED | — | — | — |

### Stage 5: Desktop app and CLI distribution (E1.4)

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| CLI-009 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| CLI-012 | desktop-cli | P0 | BLOCKED | PD-20 | — | — |
| DSK-001 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-003 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-004 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-005 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-006 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-007 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-008 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-009 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-010 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-011 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-012 | desktop-cli | P0 | NOT_STARTED | — | — | — |
| DSK-017 | desktop-cli | P0 | BLOCKED | PD-20 | — | — |
| QA-036 | qa | P0 | NOT_STARTED | — | — | — |
| CLI-011 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| CLI-013 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-002 | desktop-cli | P1 | BLOCKED | PD-21 | — | — |
| DSK-013 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-014 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-015 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-016 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-018 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-019 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| DSK-020 | desktop-cli | P1 | NOT_STARTED | — | — | — |
| OPS-023 | devops | P1 | BLOCKED | PD-21 | — | — |
| OPS-024 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-025 | devops | P1 | BLOCKED | PD-21 | — | — |
| QA-037 | qa | P1 | NOT_STARTED | — | — | — |
| CLI-014 | desktop-cli | P2 | NOT_STARTED | — | — | — |
| CLI-015 | desktop-cli | P2 | NOT_STARTED | — | — | — |
| DSK-021 | desktop-cli | P2 | NOT_STARTED | — | — | — |
| DSK-022 | desktop-cli | P2 | NOT_STARTED | — | — | — |
| OPS-026 | devops | P2 | NOT_STARTED | — | — | — |

### Stage 6: Final independent QA

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| QA-045 | qa | P0 | NOT_STARTED | — | — | — |
| FE-022 | frontend | P1 | NOT_STARTED | — | — | — |
| FE-026 | frontend | P1 | NOT_STARTED | — | — | — |
| QA-043 | qa | P1 | NOT_STARTED | — | — | — |
| FE-023 | frontend | P2 | NOT_STARTED | — | — | — |
| QA-044 | qa | P2 | NOT_STARTED | — | — | — |
| FE-028 | frontend | P3 | NOT_STARTED | — | — | — |

### Stage 7: Production readiness

| ID | Owner | Pri | Status | Blocked by | Evidence | Last commit |
|---|---|---|---|---|---|---|
| OPS-017 | devops | P0 | NOT_STARTED | — | — | — |
| OPS-018 | devops | P0 | BLOCKED | PD-11 | — | — |
| QA-046 | qa | P0 | NOT_STARTED | — | — | — |
| OPS-015 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-016 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-019 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-020 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-021 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-022 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-027 | devops | P1 | NOT_STARTED | — | — | — |
| OPS-032 | devops | P1 | NOT_STARTED | — | — | — |
| QA-034 | qa | P1 | NOT_STARTED | — | — | — |
| ARC-018 | architect | P2 | NOT_STARTED | — | — | — |
| BE-029 | backend | P2 | NOT_STARTED | — | — | — |
| OPS-028 | devops | P2 | NOT_STARTED | — | — | — |
| OPS-029 | devops | P2 | BLOCKED | PD-26 | — | — |
| OPS-031 | devops | P3 | NOT_STARTED | — | — | — |
