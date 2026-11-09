# Stage 1 review: QA (QA-006, QA-008, QA-009, QA-010 partial, QA-011)

Reviewer: zaagl-head. Date: 2026-11-09. Nothing committed; this review changes no product files.

## Verdicts

| Task | Verdict |
|---|---|
| QA-006 harness (`tests/Zaagl.Api.Tests/Harness`) | ACCEPT WITH NOTES |
| QA-008 redirect tests | ACCEPT |
| QA-009 access gate tests | ACCEPT |
| QA-010 authorization matrix (partial) | ACCEPT WITH NOTES (oracle must follow the R1 fix, below) |
| QA-011 rate limits and client IP | ACCEPT |
| Defect reports AZ-01, RD-01, SpecGap §7.3, SpecGap §7.4, F-09 | ACCEPT: each matches the spec text |

## Independence and scope

- QA touched no `src/` file: every `src/` change in `git status` belongs to the backend's BE-001/030/031 patch. QA files: `tests/Zaagl.Api.Tests/**`, `docs/phase1-implementation/qa/stage1-baseline.md`, and the `Zaagl.sln` entry.
- Ownership note for the lead: AGENT_TASKS.md gives `tests/**` (product tests) to backend and only `tests/Zaagl.Qa.*/**` to QA. `tests/Zaagl.Api.Tests` is the spec §10 project name and QA built it on the lead's instruction; record that ownership in AGENT_TASKS.md, and say who may edit `Zaagl.sln`.
- No commits.

## Do the tests assert the spec? (spot checks against `docs/spec/zaagl-spec-v9.4.md`)

| Finding | Spec text | Correct? |
|---|---|---|
| AZ-01: the enrol-only token works on user routes, including `POST /api/api-keys` | L560: enrol-only token "valid only for `POST /api/v1/auth/totp/enroll` … and `/totp/confirm`" | yes. Severity S2 is right: password-only admin access can mint an API key |
| RD-01: HEAD consumes a click | L542: "`HEAD` never counts" | yes |
| SpecGap §7.3: expired/disabled/deleted links redirect (302) instead of 410 page | L539: other states return a small static page with the status of §4.3 (404, 410 expired, 410 disabled, abuse page) | yes; the pinned current-behaviour test (never to the destination) is a sensible guard meanwhile |
| F-09: `Cache-Control: no-store` | L539: `Cache-Control: private, max-age=0` | yes |
| SpecGap §7.4: admin gets 404 on other users' links | L593-595: "owner … or Admin" | yes |
| BE-001/030/031 now pass (forged XFF, /64 unlock partition, 300/min, miss tiers incl. /56, /48, IPv4 /24, HEAD misses, 3-char path) | §7.3, §7.4, §7.13 | yes; numbers match spec L540 |

## Harness stubs: acceptable, with limits stated

1. Fake coordinator (lease and gate HTTP): acceptable; leases are not what this suite tests. Covered for real by `Zaagl.Coordinator.Tests`.
2. In-memory cache instead of Valkey: acceptable for single-instance behaviour; it hides cross-instance cache invalidation and `/health/ready`. Those stay NOT_TESTED here; record them.
3. Forward-only `OffsetClock`: fine, but `LinkService`, `TokenService`, `IdentityService` still read `DateTime.UtcNow` (seam 2), so any test that moves time across those paths is unreliable until fixed. QA already says so.
4. Only 3 hosted services kept: right for a shared DB. It means purge, safety re-check and email jobs are not covered by this suite (they are in `Zaagl.Links.Tests`). Removal by type name is brittle: a renamed job silently comes back (seam 1).
5. `RemoteIpStartupFilter`: **test-only**. It lives in the test project, is registered through `ConfigureTestServices` as an `IStartupFilter`, runs before the app pipeline (so before `UseForwardedHeaders`), sets only `Connection.RemoteIpAddress` and strips its header. It plays the TCP peer and does not bypass BE-030. Good.
6. Two direct DB writes (plan row, admin role) on the test's own new users: acceptable and documented; seam 5 removes the plan one.

None of these hides behaviour that the tests claim to cover.

## Does the matrix fail on unmapped routes?

Yes. `Every_route_has_an_oracle_entry` reads every `RouteEndpoint` from `EndpointDataSource`, and fails both for a route with no oracle entry and for an oracle entry whose route no longer exists. It ran red once when BE-031 changed the redirect regex, which proves it works.

Limits (ACCEPT WITH NOTES):
- Many entries are `Execute: false`: all of `/api/auth/*` (including `logout-everywhere` and TOTP), billing, webhooks, reports, the SignalR hub, allocator, declare-restore. Each has a reason, but none of them is tested yet. In particular, **R1 and AZ-01 are not covered by executed cases**: QA should add `logout-everywhere` with a throwaway user and the enrol-only positive cases on `/totp/*` in the next round.
- **Oracle conflict with backend rework R1:** the oracle marks `GET /api/auth/me` as `AnyAuthenticated` (an API key is "Allowed") and `logout-everywhere` / `GET /api/billing/me` the same. R1 makes them `Interactive` (or scoped) under addendum §14.4. When R1 lands, QA updates those three rules. Lead: tell both agents, so the next run doesn't report a false failure.
- "Allowed" accepts any status except 401/403/421 (and 404 where marked), so it proves authorization, not correct behaviour. That is fine for an authorization matrix.

## Evidence quality

- Two baselines (before/after the backend patch), runs 3 and 4 identical (49 failures, same set), every failing test classified, one flake explained and fixed (the aligned one-minute window: a burst across `:00` passes). Clean.
- Full solution `dotnet test` is NOT_TESTED (correctly reported). Must run once after the lead commits.
- Not re-run by the reviewer (the backend agent is building in the same checkout); evidence is the agent's TRX-based report.
- Data accumulates in the shared dev DB (`qa-api-*` users, links, keys). Acceptable until QA-001..003 (PD-24 deferral); add a cleanup script to that work.
- The baseline says the dev database is PostgreSQL 17 while `docker-compose.yml` says 18. Docker does not run on this machine, so the local server is probably a native 17 install. That contradicts my stage1-devops.md Q1 premise ("dev runs 18"); see the correction there.

## Product seams requested from backend

All six are reasonable and test-only in effect. Priority: seam 2 (TimeProvider on create/auth paths; also a spec rule, MUST use `TimeProvider`) and seam 1 (job switch) before Stage 2; the rest can follow. None is a user decision.

## Questions for the user

- **Q-A. Expired, disabled or deleted short links:** the spec says show a small "this link has expired / was disabled" page (status 410); the code redirects to the app's "expired" page or the owner's fallback URL. Options: (a) follow the spec (410 page on `zaa.gl`); (b) keep the redirect and change the spec. Recommendation: (a). Phase 1 content links need a clean 410 anyway, and search engines drop dead links faster.
- **Q-B. Should an admin be able to open and manage any user's link through the normal link pages?** The spec says yes; today admins use separate admin pages. Options: (a) keep admin pages only and change the spec; (b) build it as the spec says. Recommendation: (a). Fewer powerful routes, and admin actions stay in one audited place.
