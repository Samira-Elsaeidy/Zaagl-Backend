# Stage 2 (snippets and CLI core): paused, where we stopped

Written 2026-11-09. Stage 1 is finished and pushed (backend `9295823`, frontend `2ade5a3`). Stage 2 was started under PD-59 (Stages 2 to 7 approved, one stage at a time). All four Stage 2 agents were cut off by the usage limit (resets 12:20am Africa/Cairo), so Stage 2 is **started, not finished**. Everything below is work in progress, pushed as a WIP commit on `phase1/stage-1`.

## How far Stage 2 got (31 tasks)

| Area | Status |
|---|---|
| Architect: ARC-003 content resolution | Draft written: `design/ARC-003-content-resolution.md` (216 lines). Not reviewed by the Head. |
| Architect: ARC-010 snippet model and rendering safety | Draft written: `design/ARC-010-snippet-model.md` (386 lines). Not reviewed. |
| Architect: snippet API contract | `design/openapi-phase1.yaml` partly updated (+117/-39 lines). The agent stopped before finishing (the last step was schemas, descriptor note, delivery note, responses). `contracts.md` not yet updated. |
| Backend BE-008 snippets CRUD, versions, raw | Only the groundwork exists: `Access` module (`ContentAccessGate`, `LinkKeys`, `AccessOptions`), `SharedKernel/Security/`, `Link.cs` and `LinksDbContext` additions, `ContentLinkContracts.cs`. **No Snippets code yet** (the Snippets module is still an empty shell, no migration). |
| Backend BE-009 Markdown + diff, BE-010 CLI text ingest, BE-024 device-flow auth, BE-026 guest rate limits, BE-028 docs | Not started. |
| Frontend FE-001, FE-004 (Stage 1 partials), FE-007 to FE-012, FE-019, FE-021 | Only the dependencies were added to `package.json` (CodeMirror, highlight.js, shiki, react-markdown, remark-gfm). No pages or components yet. |
| CLI CLI-001 to CLI-010 | Scaffold started in `src/Zaagl.Cli/` (12 files: Auth, Config, Http, Infrastructure, csproj). Not in `Zaagl.sln` yet, no tests, no AOT publish check, no CI job. |
| QA (QA-016 to QA-020, QA-038) | Not started (testing deferred, PD-51). |

## State of the code when we stopped

- Backend `dotnet build Zaagl.sln`: 0 errors.
- Tests: Coordinator 9/9, Api 555/555, Links 554/555 on the second run. The failing test is `L2UnreachableApiTests.Create_update_disable_and_delete_return_their_normal_status_and_redirects_stay_fast`. It passes when run alone, so it is a timing-sensitive test on the shared test database, but it has not been investigated. The first run had 2 failures (553/555). Check these before treating Stage 2 as green.
- Frontend `tsc`: clean. Lint, unit tests and build were not re-run after the dependency change.

## What to do next (in order)

1. Resume the four agents (their work is saved; use SendMessage with the same agents) or start new ones after the limit resets. Tell them to read this file, `design/ARC-003`, `design/ARC-010` and the updated `openapi-phase1.yaml` first.
2. Architect: finish `openapi-phase1.yaml` and `contracts.md` for the snippet and device-flow endpoints. This unblocks the other three.
3. Backend: Snippets module (tables, immutable versions, public read, raw on the usercontent host), then Markdown + diff, CLI text ingest, device flow, guest limits (30/hour, 60/day), `docs/API.md`.
4. Frontend: finish FE-001 and FE-004, then editor, `/paste/new`, viewer `/p/[slug]`, gates and states, Markdown, versions and diff, Arabic/RTL. Keep 0 CSP violations.
5. CLI: add to `Zaagl.sln`, finish login/logout/whoami (device flow), token refresh lock, `link`, `paste` (UTF-8 and UTF-16), `ls`, `config`; add a CI matrix job; try a Windows AOT publish.
6. Head reviews everything, then the lead brings the results and questions to the product owner. No commit without approval.

## Rules that still apply

- The product owner is the lead: ask before any commit, branch, push, worktree or workflow change.
- Agents work directly in `C:\Users\pc\Zaagl-Backend` and `C:\Users\pc\Zaagl-Frontend`.
- Leave the graphify changes and the local `.claude/settings*` and `.claude/hooks/` out of commits.
- Testing tasks stay deferred (PD-51); engineers still keep all suites green.
- Stages 3 to 7 follow Stage 2, one at a time, with a check-in between stages.

## Open questions for the product owner

None new from Stage 2 yet (the agents stopped before reporting). The Stage 1 list in `SESSION_HANDOFF.md` is still open.
