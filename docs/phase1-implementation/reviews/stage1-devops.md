# Stage 1 review: DevOps (OPS-003 partial, OPS-004, OPS-005, OPS-006, OPS-014)

Reviewer: zaagl-head. Date: 2026-11-09. Nothing committed; this review changes no product files.

## Verdicts

| Task | Verdict | Owner of fix |
|---|---|---|
| OPS-003 Image hygiene (partial) | ACCEPT WITH NOTES | devops |
| OPS-004 backend-ci | **REWORK** (one-line fix) | devops |
| OPS-005 Migrations check | ACCEPT WITH NOTES | devops |
| OPS-006 frontend-ci | ACCEPT WITH NOTES | devops |
| OPS-014 Secrets README | ACCEPT | devops |

## Scope and commits

- Backend HEAD is `9923526` (unchanged). Frontend HEAD is `985948d`, **not** `2c70b59`. Reflog: `clone 2c70b59` -> `commit 985948d` on `main` at 01:08 (+0200) -> `checkout phase1/stage-1`. The commit adds only `src/Doc/*` (PRD, spec, tech stack copies), is already on `origin/main`, and predates the stage branch. Not a DevOps commit; the "expected 2c70b59" in the brief is stale. See question Q3.
- DevOps-owned files changed: `.github/workflows/backend-ci.yml`, `.config/dotnet-tools.json`, `.dockerignore`, `src/Zaagl.Api/Dockerfile`, `src/Zaagl.Coordinator/Dockerfile`, `deploy/secrets/README.md`, `Zaagl-Frontend/.github/workflows/frontend-ci.yml`. All within devops ownership (AGENT_TASKS.md: `deploy/**`, `**/Dockerfile`, `.github/workflows/**`); `.config/dotnet-tools.json` and `.dockerignore` are not listed but are CI/image plumbing and named in OPS-003/005. Fine.
- Other modified files are not from DevOps: `Zaagl.sln` (adds `Zaagl.Api.Tests`, QA), `DECISIONS.md` (lead, PD-14 update), `.gitignore` + `.gitattributes` (graphify tooling), frontend `package.json`/lock (frontend tooling).
- Observation for the lead (not DevOps): `git worktree list` shows four worktrees under `.claude/worktrees/` with branches `worktree-agent-*` at `833963a` (one locked). The standing rule forbids branches/worktrees without the user's approval. See Q4.

## OPS-004 backend-ci: REWORK

**Defect (blocking):** the Build step is `dotnet build ... 2>&1 | tee build.log`. A `run:` step without an explicit `shell:` runs as `bash -e {0}` on GitHub (no `pipefail`), so the pipeline's status is `tee`'s, always 0. **A compile error leaves the Build step green**; the job only goes red later and less clearly (migrate/test with `--no-build` on missing assemblies). The plan's gate ("the build is the gate") is not met.

Exact fix (devops): either add `set -o pipefail` as the first line of the Build step's script, or add `defaults: run: shell: bash` at workflow level (GitHub then runs `bash --noprofile --norc -eo pipefail {0}`). If pipefail is enabled globally, also make the two warning counters tolerant of zero matches (`grep ... || true`), otherwise the step fails the day the last RS0030 warning is fixed.

What is right:
- Connection reproduces the tests exactly: every test file hard-codes `Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev` (e.g. `tests/Zaagl.Links.Tests/PurgeJobTests.cs:21`, `tests/Zaagl.Coordinator.Tests/StartupCheckTests.cs:30`); the service publishes `5433:5432` with `POSTGRES_DB/USER/PASSWORD` = `zaagl/zaagl/zaagl_dev`. QA's `tests/Zaagl.Api.Tests/Harness/TestInfrastructure.cs:18` reads `ZAAGL_TEST_PG` with the same default.
- Migrate mode (`src/Zaagl.Api/Program.cs:211-220`) builds the full DI graph first, so lines 31-38 apply: `ConnectionStrings:Postgres`, `ConnectionStrings:Valkey` (presence only) and a Jwt key of 32+ chars are required. The workflow sets all three. Valkey service is provided for tests (HybridCache L2).
- `deploy/init-db/01-extensions-and-roles.sql` (btree_gist, roles) is applied with psql as the `zaagl` superuser before migrate. Coordinator schema `alloc` is created by the coordinator itself (`AllocSchema.EnsureCreatedAsync`), so no extra step is needed.
- `coverlet.collector` is referenced by every test project, so `--collect "XPlat Code Coverage"` works. TRX uploaded with `if: always()`.
- `permissions: contents: read`; no secrets used.

Notes (non-blocking, devops follow-ups):
1. Plan §5 row 1 also lists `-warnaserror`, `dotnet format --verify-no-changes` and `dotnet list package --vulnerable`. The first is consciously dropped (A19, documented in the file). The other two are missing and not mentioned in the report: add `dotnet list package --vulnerable --include-transitive` now (cheap, fail on findings) and record `dotnet format` as deferred in STATUS.
2. The connection string, Valkey and Jwt key are set at workflow `env:` level, so they leak into every test host. A test that forgets its own config would pass in CI and fail locally. Move them to the migrate steps' `env:`.
3. `on: push` + `pull_request` runs twice for PR branches. Acceptable; optionally limit `push` to `main`.
4. PostgreSQL 17 in CI vs `postgres:18` in `docker-compose.yml`. PD-26 (OPEN) says "dev and CI currently test on 17", which is wrong for dev. Q1.
5. Flaky shared-DB failures are expected until QA-001..003 (PD-24 deferral); no retry was added, which is correct (QA records them).

## OPS-005 migrations-check: ACCEPT WITH NOTES

- Discovers contexts under `src/Modules` by regex; matches all four (`Billing`, `Identity`, `Links`, `Analytics` DbContext, primary-constructor form). Fails if none found. Walks up to the owning `.csproj` (e.g. `src/Modules/Links/Zaagl.Modules.Links.csproj`).
- `dotnet ef migrations has-pending-model-changes` exits non-zero when the model differs (EF Core 8+), and the loop turns that into a job failure with a fix hint. **Not demonstrated** with a real pending change (NOT_TESTED): request one throwaway run on GitHub with a dummy property added, before the user relies on it.
- Migrate twice and compare `__ef_migrations` row counts per schema; second run must be a no-op. Sound.
- Re-run by reviewer: `dotnet tool restore` -> "Restore was successful"; `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/Modules/Links/Zaagl.Modules.Links.csproj --startup-project src/Zaagl.Api/Zaagl.Api.csproj --context LinksDbContext --configuration Debug --no-build` -> "No changes have been made to the model since the last migration.", exit 0.
- Not in this job yet (plan §5 row 2): EF bundles and "previous release's tests against new schema". Fine to defer; record in STATUS.

## OPS-003 image hygiene (partial): ACCEPT WITH NOTES

- `.dockerignore` excludes `.git`, `.claude` (includes the worktrees), bin/obj, test output, graphify, `.env*`, `deploy/secrets/*` except README, `*.pfx`, `*.key`. No build step depends on `.git` (no SourceLink/GitVersion in props/csproj).
- Fixed UID/GID 10001, numeric `USER 10001:10001`, `COPY --chown`. Consistent with DEVOPS_PLAN §2.2.
- API writable paths: none needed. The old `useradd -r` also had no home directory, so DataProtection behaviour (ephemeral keys) is unchanged.
- Coordinator `/state` (`coordstate` named volume, `Coordinator__StateFilePath=/state/hwm.json`): a **new** volume copies the image's `/state` ownership (10001), so it works. An **existing** dev volume keeps the old system UID and the coordinator will fail to write `hwm.json`. Documented only in a Dockerfile comment; add one line to `docs/RUNBOOK.md` or the root README (devops, before anyone rebuilds images) with the fix: `docker compose down coordinator && docker run --rm -v <project>_coordstate:/state alpine chown -R 10001:10001 /state`, or delete the dev volume.
- Not done (agent said partial): digest pinning of `sdk:10.0`/`aspnet:10.0`; curl kept (documented). Build context size and `id zaagl` = 10001 are **NOT_TESTED** (no Docker on this machine); verify in OPS-015 CI build.

## OPS-006 frontend-ci: ACCEPT WITH NOTES

- `npm ci`, `npm run lint` (eslint), `next typegen` + `tsc --noEmit`, `next build`, Node 24, npm cache. Public env placeholders match `.env.example`; no secrets.
- `npm run build` is **NOT_TESTED** locally; first run on GitHub is the evidence.
- `npm ci` depends on the frontend agent's in-progress `package.json`/`package-lock.json` (vitest, playwright, msw, @types/node 24) being in sync at commit time.
- Test step commented out until `npm test` exists; the frontend agent is adding it now, so enabling it should be part of Stage 1 close, not later.

## OPS-014 secrets README: ACCEPT

- Only names, purposes and generation commands; no real keys. CI JWT key is an obvious dummy (`ci-only-signing-key-not-a-secret-...`).
- Consistent with approved decisions: PD-03 (subdomain, CF token scoped to `zaa.gl`), PD-05 (30-minute ticket window), PD-12 (tus on `api.zaa.gl`), PD-14 (Turnstile required for guests), PD-23 (BFF session keys). Items that depend on open choices are marked conditional (`UPLOAD_GRANT_KEY`, `LINK_KEY_PEPPER`).

## Evidence run by the reviewer

- `git log -1` both repos; `git reflog` and `git show --stat 985948d` in frontend; `git worktree list`.
- PyYAML: both workflows parse; jobs `['build-test', 'migrations-check']` and `['build']`.
- EF pending-model check for `LinksDbContext` (above), exit 0.
- Not run (as instructed): solution build, `npm run build`, Docker.

## Questions for the user

- **Q1. Which PostgreSQL version should CI test on?** Dev compose says 18, the spec says 17, CI uses 17, PD-26 is open. **Correction (after the QA review):** the database the tests actually run against on this machine is reported as PostgreSQL 17 (QA baseline; Docker cannot run here, so compose's 18 is not what developers use). Options: (a) 17 everywhere (spec, CI and the real dev DB agree; change compose to 17); (b) 18 everywhere; (c) keep the mismatch until Stage 7. Recommendation: (a) keep CI on 17 now, and record PD-26 before backup work.
- **Q2. Should we prove the safety checks fail when they should, on GitHub, before relying on them?** Options: (a) yes, one throwaway PR with a deliberate compile error and a model change without migration, then close it (needs your approval to push a branch); (b) no, trust the reasoning. Recommendation: (a).
- **Q3. The frontend has a commit `985948d` (adds the PRD/spec/tech-stack docs under `src/Doc`) already pushed to `origin/main`. Was that you?** Options: (a) yes, mine, keep it as the stage base; (b) not mine, investigate. Recommendation: (a) if you recognise it; nothing else is needed. **Answered 2026-11-09: made by the user. Closed.**
- **Q4. Four agent worktrees and `worktree-agent-*` branches exist in the backend repo.** Options: (a) approve them retroactively and delete them at stage end; (b) delete now (after checking nothing unique is in them). Recommendation: (a), then remove at Stage 1 close.

## CI recheck after the DevOps fix round (2026-11-09)

Verdict: **OPS-004 rework ACCEPTED (ACCEPT WITH NOTES).** File re-read in full; PyYAML parse OK (`defaults: {run: {shell: bash}}`, jobs `build-test`, `migrations-check`).

- **pipefail:** workflow `defaults: run: shell: bash`, so every `run:` step is `bash --noprofile --norc -eo pipefail {0}`. Build failure now fails the Build step. Both warning counters use `{ grep ... || true; }`, so zero warnings no longer fails. Other pipelines checked under pipefail: DbContext discovery (`find | head -n1 || true`; `for ... in $(...)` and `< <(...)` do not trigger errexit), psql history queries (no pipes). Coherent.
- **Vulnerable packages:** `dotnet list Zaagl.sln package --vulnerable --include-transitive --format json` parsed with jq; fails only on High/Critical, lists Low/Moderate in the job summary. Logic is sound; `dotnet list` failing (for example NuGet unreachable) also fails the step, which is the safe direction. **NOT_TESTED** locally (no jq on this machine, no bash run); the first GitHub run is the evidence. Severity threshold (High/Critical) is a reasonable default, not a user decision.
- **Env vars:** app config (`ConnectionStrings__*`, `Jwt__Key`, `ASPNETCORE_ENVIRONMENT`) now only on the migrate steps (both jobs) and the Test step; workflow level keeps only `PG*` for psql and the dotnet flags. Note 2 resolved. The Test step still sets them for every test host; acceptable (the tests set their own config and QA's harness overrides with `UseSetting`).
- **Coordinator volume:** `docs/RUNBOOK.md` gained "Existing `coordstate` volume after the image UID change (one-time)" with the `chown -R 10001:10001` command. Note resolved.
- Still open (non-blocking): `dotnet format` deferred (stated in the header comment); `on: push` + `pull_request` double runs; the header comment says "dev compose uses postgres:18" (true of the file) pending Q1/PD-26; the negative proof run (Q2) has not happened.
