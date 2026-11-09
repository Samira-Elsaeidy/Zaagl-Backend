# Session handoff

Read this first in every new session, then STATUS.md, DECISIONS.md (Approvals table at the end), IMPLEMENTATION_PLAN.md and AGENT_TASKS.md.

## Current state (2026-11-09, end of day)

| | |
|---|---|
| Stage | **Stage 1 groundwork complete, reviewed, committed and pushed** on branch `phase1/stage-1` in both repos (user approved "Commit and push", 2026-11-09). Stages 2 to 7 are ON HOLD (PD-50/PD-51). |
| In progress | Nothing. PD-58 finished: build 0 errors; Coordinator 9/9, Links 550/550, API 555/555; frontend 84/84 (2026-11-09). |
| Next exact action | Ask the user before starting Stage 2. Run CI on GitHub / Docker on the user's Docker machine when they choose. |
| Final review | docs/phase1-implementation/reviews/stage1-final.md (Head): 33 DONE, 5 PARTIAL, 0 NOT DONE; the pre-commit migration rework is fixed. |

## Standing rules from the user (also in memory)

- The user is the project lead and responsible: ask before any commit, branch, push, worktree, or change in how agents work.
- **No commits until the user explicitly says so** (each commit needs its own approval).
- Agents work directly in C:\Users\pc\Zaagl-Backend and C:\Users\pc\Zaagl-Frontend, each only in its own files (AGENT_TASKS.md). The zaagl-head agent reviews every piece and collects questions; the lead relays questions to the user unchanged.
- Testing tasks are deferred (PD-51), but engineers keep the existing tests green.
- Notify the user's phone (PushNotification) only when a decision or permission is needed or a round is ready for review.

## What to commit (when approved)

- Zaagl-Backend: all Stage 1 changes under src/, tests/, docs/ (phase1-implementation incl. reviews, design, adr, qa; docs/spec/zaagl-phase1-addendum.md; RUNBOOK, API), .github/workflows/backend-ci.yml, .config/dotnet-tools.json, .dockerignore, Dockerfiles, docker-compose.yml, deploy/secrets/README.md, .claude/agents/zaagl-head.md, .claude/agent-dashboard.config.json.
- Zaagl-Frontend: all Stage 1 changes under src/, e2e/, docs/, config files (next.config.ts, vitest.config.mts, playwright.config.ts, package.json, package-lock.json, .env.example), .github/workflows/frontend-ci.yml.
- **Leave out (user decision):** the graphify changes (.gitignore lines, .gitattributes, .graphifyignore, docs/graphify/, tools/) in both repos; .claude/settings.json, .claude/settings.json.bak and .claude/hooks/ (local dashboard tooling).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Last test results (2026-11-09, uncommitted tree)

- Backend: build 0 errors; Coordinator 9/9; Links 550/550; API (QA) 555/555. Known flake: EmailJobsTests.Queued_confirm_email_is_sent_with_the_fragment_url (~1 in 20 on the shared test DB).
- Frontend: tsc and lint clean; 84/84 unit tests; production build OK; headless check: 0 CSP violations on 19 routes (en and ar), 0 hydration errors in Arabic.
- Not run: CI on GitHub; anything Docker-based; real Google/GitHub sign-in (no provider keys).

## Environment

| Item | State / how to start |
|---|---|
| PostgreSQL 17 (portable) | `C:\Users\pc\tools\pgsql\bin\pg_ctl.exe -D C:\Users\pc\tools\pgdata17 -l C:\Users\pc\tools\pg.log start` (port 5433, zaagl / zaagl_dev, WAL archiving to C:\Users\pc\tools\pgarchive) |
| Local dev DB | Migrations applied through Links ValidateLinkTargetKindChecks, Resources Initial, Files tasks, Identity one-time codes. Allocator reset on 2026-11-09 with the user's approval (pool position 1,000,000; installed with the dev key fingerprint `c4d0005be7b8054b344e7d62ca6d1627a49c9bd1d3fd569fcf8ed5382b659708`). Tests share this DB and can break the allocator again (test isolation deferred). |
| Run the app locally | Coordinator: `dotnet run --project src/Zaagl.Coordinator --launch-profile http` (5085). API: `dotnet run --project src/Zaagl.Api --launch-profile http` with `ConnectionStrings__Postgres` set (5084). Web: in Zaagl-Frontend, `NEXT_PUBLIC_API_URL=http://localhost:5084 ZAAGL_INTERNAL_API_URL=http://localhost:5084 WEB_SESSION_KEYS=k1:<32 random bytes base64url> npx next dev -p 3000`. Valkey is not installed here: the API now degrades gracefully (redirects ~10 ms after the first). On a Docker machine use `docker compose up -d --build` (includes Valkey) plus the one-time coordinator init. |
| Docker | Not available on this PC (firmware virtualisation off, no admin). |
| Agent dashboard | `node "C:\Users\pc\Downloads\agent-pixel-dashboard\server.js" "C:\Users\pc\Zaagl-Backend"` → http://localhost:4317 (Claude-style pixel agents, Head pinned as main card, click a card for details). |
| Scratch DB | `zaagl_fe_pd41` (created by a frontend agent for a live check) still exists on the dev server; drop it only if the user agrees. |

## Open questions for the user (none block the commit)

What a retried share returns (ARC-002 Q1); content shares in the main link list (ARC-002 Q2); PD-08 (content vs Free's monthly link cap); storage at takedown and reinstate (ARC-006 Q2/Q3); `resources:write` scope; CLI sign-in on servers (U-7); Arabic-Indic digits (U-9); staging hostnames (U-10); `www` redirect (U-11); accounts and purchases (U-12); Cloudflare terms PD-52 (before launch).

## Resume checklist

1. Read this file, STATUS.md and the DECISIONS.md Approvals table.
2. `git status` / `git log -3` in both repos; compare with STATUS.md.
3. Start PostgreSQL and the dashboard if needed.
4. Continue with "Next exact action" above; never commit without the user's explicit approval.
