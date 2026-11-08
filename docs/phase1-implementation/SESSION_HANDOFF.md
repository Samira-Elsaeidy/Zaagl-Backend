# Session handoff

Read this first in every new session, then STATUS.md, IMPLEMENTATION_PLAN.md, AGENT_TASKS.md and DECISIONS.md.

## Current state (2026-11-09)

| | |
|---|---|
| Stage | **Planning complete. The product owner answered 12 decisions on 2026-11-09 (DECISIONS.md, Approvals). Stage 1 not started.** |
| Current task | None in progress |
| Phase 1 code | **None exists.** Resources, Snippets, Files and Media modules are empty; frontend Phase 1 feature files are empty; no desktop or CLI repo. |
| Next exact action | Start Stage 1 in parallel: ARC-001 contract pack, which also writes the Phase 1 spec addendum (PD-34) with the approved decisions; BE-001 (API-key scopes), BE-030 (forwarded headers), BE-031 (redirect hardening); FE-001, FE-002, FE-027 then FE-029 (BFF session); OPS-004/005/006 (CI). QA-001…QA-003 (test isolation) are deferred by the product owner. |
| Approved scope changes | No Pro tier (max file 100 MB on Starter); user bytes on a `zaa.gl` subdomain; guests may create snippets and uploads; bytes kept through the 90-day quarantine; desktop shortcut Ctrl+Alt+Z / Cmd+Ctrl+Z. |

## Completed this session

1. **Phase 0 analytics** (before planning): click event list, UTM report, partition-bounds fix. Commit `1effe01` on branch `feat/phase0-click-events-utm`, pushed to GitHub. Not merged to `main`.
2. **Agent dashboard** installed into `Zaagl-Backend/.claude` and improved (see AGENT_DASHBOARD.md).
3. **Six parallel planning audits** (architect, backend, frontend, devops, desktop/CLI, QA) → `_drafts/`.
4. **Consolidated plan**, 13 documents in this folder; 195 tasks; 35 decisions.

## Files changed this session (not committed)

| Path | What |
|---|---|
| `Zaagl-Backend/docs/phase1-implementation/*.md` | the 13 plan documents |
| `Zaagl-Backend/docs/phase1-implementation/_drafts/*` | raw agent drafts (source material) and the Phase 0 status snapshot |
| `Zaagl-Backend/.claude/agents/zaagl-*.md` | six agent definitions |
| `Zaagl-Backend/.claude/agent-dashboard.config.json` | dashboard names, colours, en/ar labels |
| `Zaagl-Backend/.claude/hooks/agent-dashboard-notify.js`, `.claude/settings.json` | dashboard hooks (backup: `settings.json.bak`) |
| `C:\Users\pc\Downloads\agent-pixel-dashboard\server.js`, `public\index.html` | dashboard changes (outside the repos) |

**Not from this session, left untouched:** `.gitignore` changes, `.gitattributes`, `docs/graphify/`, `tools/` (backend) and `.gitignore`, `.gitattributes`, `.graphifyignore` (frontend). They come from a separate `graphify` session; check with the user before committing them.

## Last test results

- Backend, 2026-11-09, PostgreSQL 17 at `localhost:5433`: 274 tests, 272 passing at baseline. After commit `1effe01`, all 274 passed. One known flaky test (`AdminApiTests.Owner_links_disabled_and_restored_as_user_disabled_kind_only`); cause in SECURITY_AND_QA.md §1.3, fix is QA-002/QA-003.
- Frontend: no test framework yet (FE-002, QA-039).

## Environment

| Item | State | How to start |
|---|---|---|
| PostgreSQL 17 (portable, no admin needed) | **Stopped** | `C:\Users\pc\tools\pgsql\bin\pg_ctl.exe -D C:\Users\pc\tools\pgdata17 -l C:\Users\pc\tools\pg.log start` (port 5433, user `zaagl` / `zaagl_dev`, WAL archiving to `C:\Users\pc\tools\pgarchive`) |
| Migrations | Applied up to `AddClickEventUtm` | `ConnectionStrings__Postgres="Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev" dotnet run --project src/Zaagl.Api -- migrate` |
| Docker | **Not available.** Firmware virtualisation is off and the account is not admin. | Enable virtualisation in the BIOS, install Docker Desktop as admin |
| Agent dashboard | Running in this session's background; stops when the session ends | `node "C:\Users\pc\Downloads\agent-pixel-dashboard\server.js" "C:\Users\pc\Zaagl-Backend"` then open http://localhost:4317 |
| Node.js | 24.12 | — |
| .NET SDK | 10.0.101 | — |

## Dashboard state and restart

- Hooks and the six agent definitions are installed in `Zaagl-Backend/.claude`. A new session started in `Zaagl-Backend` loads the `zaagl-*` agent types, so each agent appears on its own named card.
- The server keeps no state on disk. After a restart, cards fill again from hook events and from agent transcripts written in the last 6 hours.

## Blockers

See IMPLEMENTATION_PLAN.md §10: 11 decisions before Stage 1, the user-content domain purchase, code-signing procurement, local Docker, and a staging host.

## Resume checklist

1. Read this file, STATUS.md, IMPLEMENTATION_PLAN.md, AGENT_TASKS.md, DECISIONS.md.
2. `git status` and `git log -3` in both repos; compare with "Last verified commit" in STATUS.md.
3. Check DECISIONS.md "Approvals" for what has been approved since.
4. Start PostgreSQL and the dashboard if needed.
5. Continue with the next unblocked task in STATUS.md; do not redo VERIFIED tasks.
6. After each task: update STATUS.md (status, evidence, commit), this file, and DECISIONS.md if anything was decided.
