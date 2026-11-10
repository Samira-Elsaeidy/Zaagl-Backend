# Phase 1 feature plan (features first, everything else paused)

Written 2026-11-10 at the product owner's request. This plan replaces the stage-by-stage order of `IMPLEMENTATION_PLAN.md` for as long as it is active. Task IDs, owners and detail documents are unchanged; only the order and the scope change.

## Rules

1. **Only the four Phase 1 features are built:** E1.1 snippets, E1.2 images and media, E1.3 ephemeral files, E1.4 desktop app and CLI.
2. **Easy and visible first.** Each step ends with something a person can use. The hardest parts (burn race, desktop signing) come last, after the pieces they depend on exist.
3. **Everything not listed below is PAUSED until the four features are finished** (see "Paused").
4. **Each agent stays in its own files** (AGENT_TASKS.md, file ownership table). Backend and frontend work in parallel against the OpenAPI contract with mocked APIs.
5. **Ask the product owner before any commit, branch, push or worktree.** Keep graphify changes and local `.claude/settings*` out of commits.
6. Engineers keep the existing test suites green. Dedicated QA tasks stay paused except where listed.

## Step 0: unblock (do first, about a day)

| # | Task | Agent | Notes |
|---|---|---|---|
| 0.1 | Rename and review migration `AddContentLinkColumns` (it adds `links.lifecycle_outbox`); put the migrator's `ASPNETCORE_ENVIRONMENT=Development` into `docker-compose.yml` | backend, devops | Backend currently starts only with a local override file |
| 0.2 | Forgot-password and reset-password pages (backend endpoints already exist) | frontend | `IdentityEndpoints.cs:66-77`; en and ar strings |
| 0.3 | Investigate the flaky `L2UnreachableApiTests` and the `EmailJobsTests` flake | backend | Only so suites can be trusted |
| 0.4 | Finish `openapi-phase1.yaml` and `contracts.md` for snippets and device flow | architect | Unblocks all other agents |

## Step 1: E1.1 snippets in the browser

| Task | Title | Agent |
|---|---|---|
| ARC-003, ARC-010 | Review and finalise the two drafts (ARC-010 is BLOCKED on PD-09, needs a product decision) | architect |
| BE-008 | Snippets CRUD, versions, public read, raw | backend |
| BE-026 | Guest snippet rate limits (30/hour, 60/day) if approved | backend |
| FE-007 | Highlighting module | frontend |
| FE-008 | Snippet editor, language picker, detection | frontend |
| FE-009 | `/paste/new` page | frontend |
| FE-010 | Snippet viewer `/p/[slug]` | frontend |
| FE-012 | Viewer gates and states (password, expired, burned) | frontend |

**Done when:** a person pastes code in the browser, gets a zaa.gl link and another person opens it with highlighting.

## Step 2: E1.1 polish

| Task | Title | Agent |
|---|---|---|
| BE-009 | Markdown and diff | backend |
| FE-011 | Markdown rendering | frontend |
| FE-019 | Versions and diff UI | frontend |
| FE-021 | Arabic and RTL completeness | frontend |
| BE-028 | API docs and OpenAPI | backend |
| QA-017, QA-018 | Markdown sanitizer corpus, snippet XSS test | qa (kept: security-critical) |

**Done when:** Markdown renders safely, versions and diff work, the viewer is correct in Arabic.

## Step 3: E1.1 command line

| Task | Title | Agent |
|---|---|---|
| BE-024 | Device-flow auth for CLI and desktop | backend |
| (activation page) | `/activate` page for the device code (FE-ACTIVATE) | frontend |
| BE-010 | CLI text ingest | backend |
| CLI-001 to CLI-004 | Scaffold, command framework, HTTP client, credential stores | desktop-cli |
| CLI-005, CLI-006 | login, logout, whoami; token refresh with lock | desktop-cli |
| CLI-007, CLI-008 | `link`, `paste` from stdin | desktop-cli |
| CLI-010 | `ls` and `config` | desktop-cli |

**Done when:** `echo hi | zaagl paste` returns a working link on Windows.

## Step 4: E1.2 images and media

Simple images first; video, PDF and SVG after.

| Task | Title | Agent |
|---|---|---|
| ARC-009 | Upload pipeline design (tus, scan, promote) | architect |
| BE-011, BE-012 | tus upload sessions; finalize and file identification | backend |
| BE-014 | ClamAV scan and quarantine (fail closed) | backend |
| BE-017 | Download tickets and delivery from the content domain | backend |
| OPS-007 to OPS-012 | Compose, worker, ClamAV, Nginx, host, disk limits (minimum needed to run uploads) | devops |
| FE-013 | Uploader | frontend |
| FE-014 | Files and media library `/files` | frontend |
| FE-015 | Image viewer `/i/[slug]` (background switcher, zoom) | frontend |
| BE-015, BE-016 | Media metadata and derivatives; SVG safe view | backend |
| FE-016 | Video player for recordings | frontend |

**Done when:** an image uploaded in the browser is served byte for byte (same SHA-256) and an infected file is never served.

## Step 5: E1.3 ephemeral files

| Task | Title | Agent |
|---|---|---|
| BE-018 | Burn after downloading (exactly once, even with parallel requests) | backend |
| BE-019 | Expiry and purge, link coupling | backend |
| BE-021 | Quotas endpoint and ledger | backend |
| FE-017 | Download page `/f/[slug]` with burn | frontend |
| FE-018 | PDF preview | frontend |
| FE-020, FE-025 | Owner detail pages; library integration in `/links` | frontend |
| BE-020, BE-022 | Orphan cleanup; content abuse and takedown | backend |
| QA-030 | Burn race suite | qa (kept: this is the riskiest feature) |

**Done when:** a file up to the plan limit uploads, downloads once and then is gone, and expiry removes it.

## Step 6: E1.4 desktop app and CLI uploads

| Task | Title | Agent |
|---|---|---|
| CLI-009 | `share <path>` with resumable upload | desktop-cli |
| DSK-001, DSK-003 to DSK-006 | Tauri scaffold, API client, sign-in, token manager, tray | desktop-cli |
| DSK-007, DSK-008 | Global shortcut with conflict handling; clipboard classifier | desktop-cli |
| DSK-009 to DSK-012 | Share URL, text, upload engine, file and screenshot | desktop-cli |
| CLI-012, DSK-017, OPS-024 | Signing and notarisation (needs certificates, PD-20) | desktop-cli, devops |
| DSK-013 to DSK-016, DSK-018 | Chime, offline UX, settings, autostart, auto-update | desktop-cli |

**Done when:** the shortcut shares a clipboard URL, text, screenshot or file and puts the new link on the clipboard, in a signed build.

## Start now in parallel (long lead, owner decisions)

| Item | Needed for |
|---|---|
| PD-20 code signing certificates | Step 6 |
| PD-09 (unblocks ARC-010) | Step 1 |
| PD-52 Cloudflare terms decision | Step 4 |
| PD-11 EU legal review | public launch of uploads |
| PD-27 staging host | performance checks |

## Paused until all four features are finished

- Stage 1 leftovers: partial FE-001, FE-004, OPS-003, QA-010 and the 11 deferred testing tasks (PD-51).
- Stage 6 in full: independent QA pass, pen-test (QA-045), accessibility (FE-022), traceability, mutation testing, bundle budgets, security regression suite.
- Stage 7 in full: production compose, backups and restore drill, monitoring, alerts, staging, load tests, zero-downtime deploy, runbooks, Valkey and PostgreSQL decisions.
- Feature-adjacent extras: CLI-011, CLI-013 to CLI-015, DSK-002, DSK-019 to DSK-022, OPS-023 to OPS-026, OPS-030, BE-023, BE-025, BE-029, ARC-007, ARC-011, ARC-013, ARC-015, ARC-017, ARC-018, FE-024, FE-028, QA tasks not listed above.

When Step 6 is done, un-pause in this order: Stage 6, then Stage 7.

## Agent workload summary

| Agent | Steps |
|---|---|
| architect | 0.4, 1, 4 (design tasks only) |
| backend | 0.1, 0.3, 1, 2, 3, 4, 5 |
| frontend | 0.2, 1, 2, 3 (activate page), 4, 5 |
| desktop-cli | 3, 6 |
| devops | 0.1, 4 (minimum), 6 (signing) |
| qa | only QA-017, QA-018, QA-030 until the features are finished |
| head | reviews each step before the lead reports to the product owner |
