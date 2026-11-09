# Stage 1 QA baseline: `tests/Zaagl.Api.Tests` (QA-006, QA-008, QA-009, QA-010 start, QA-011)

| | |
|---|---|
| Owner | zaagl-qa (independent; wrote tests and evidence only, no `src/` changes) |
| Date | 2026-11-09 |
| Checkout | `C:\Users\pc\Zaagl-Backend`, branch `phase1/stage-1`, HEAD `9923526`, uncommitted (nothing added or committed by QA, per the lead's instruction) |
| Database | shared dev PostgreSQL 17, `localhost:5433/zaagl` (override: `ZAAGL_TEST_PG`) |
| Command | `dotnet test tests/Zaagl.Api.Tests` (TRX: `--logger "trx;LogFileName=runN.trx"`) |

## 1. Results

The backend agent's BE-001 / BE-030 / BE-031 edits landed in the shared checkout while QA was running, so there are two baselines:

- **Before:** run 1, src as at `9923526`, no backend edits in `src/Zaagl.Api`.
- **After:** runs 3 and 4, with the uncommitted backend edits in place: `Program.cs`, `ResolutionEndpoints.cs`, `LinksEndpoints.cs`, `AnalyticsEndpoints.cs`, `ApiResults.cs`, `IpPartition.cs`, `appsettings.json`, plus the new `ApiScopes.cs`, `ForwardedHeadersSetup.cs` and `RedirectRateLimiter.cs`.

Run 2 was taken while those edits were landing and is not used, apart from the boundary flake explained in §3.

| Run | Total | Passed | Failed | Duration |
|---|---|---|---|---|
| 1 (before) | 513 | 412 | **101** | 37 s |
| 3 (after) | 513 | 464 | **49** | 48 s |
| 4 (after, repeat) | 513 | 464 | **49** (same set as run 3) | 48 s |

| Area (class) | Tests | Before: pass / fail | After: pass / fail |
|---|---|---|---|
| QA-006 harness (`HarnessSmokeTests`) | 2 | 2 / 0 | 2 / 0 |
| QA-008 redirect (`RedirectEndpointTests`) | 33 | 28 / 5 | 29 / 4 |
| QA-009 AccessGate (`AccessGateTests`) | 12 | 11 / 1 | 11 / 1 |
| QA-011 rate limits and client IP (`RateLimitAndClientIpTests`, `TrustedProxyTests`) | 15 | 5 / 10 | 15 / 0 |
| QA-010 authz matrix (`AuthorizationMatrixTests`) | 451 | 366 / 85 | 407 / 44 |

Authorization matrix by family (each is its own theory and trait):

| Family | Cases | Before | After |
|---|---|---|---|
| `Matrix_core` (expectations believed met today) | 363 | 363 PASS | 363 PASS |
| `Matrix_api_key_scope_is_enforced`, Defect=BE-001 | 40 (+1 headline fact) | 0 PASS | **40 PASS** (+ headline PASS) |
| `Matrix_enrol_only_token_is_refused`, Defect=AZ-01 (new) | 26 | 2 PASS* | 2 PASS* |
| `Matrix_admin_can_use_owner_routes`, SpecGap=7.4 | 20 | 0 PASS | 0 PASS |

\*The two passes were public routes misfiled in this family. They have since been reclassified into `Matrix_core`, so the next run shows 24 enrol-only cases and 365 core cases.

## 2. Every failing test, with reason and classification

### Fixed by the backend agent (failed before, pass after): expected defects

| Test | Before | Defect |
|---|---|---|
| `Api_key_with_only_links_read_cannot_create_a_link` | 201 Created | BE-001 |
| `Matrix_api_key_scope_is_enforced` (40 cases) | 200 / 201 for a key without the route's scope | BE-001 |
| `Forged_x_forwarded_for_from_an_untrusted_peer_does_not_change_its_partition` | 11 × 401, never 429 (forged XFF gave a new partition each time) | F-02 / BE-030 |
| `Forged_x_forwarded_for_cannot_make_two_peers_share_a_partition` | Victim got 429 after 10 attackers sent `X-Forwarded-For: <victim>` | F-02 / BE-030 |
| `Addresses_rotating_inside_one_ipv6_64_share_one_unlock_partition` | 11 × 401 | F-03 / BE-031 |
| `Redirects_are_limited_to_300_per_minute_per_partition` | 302 × 301 | F-03 / BE-031 |
| `Misses_are_limited_to_30_per_minute_per_ipv4_address` | 404 × 31 | F-03 / BE-031 |
| `Misses_are_limited_to_30_per_minute_per_ipv6_64` | 404 × 31 | F-03 / BE-031 |
| `Misses_are_limited_to_120_per_minute_per_ipv6_56` | 404 × 121 | F-03 / BE-031 |
| `Misses_are_limited_to_300_per_minute_per_ipv6_48` | 404 × 301 | F-03 / BE-031 |
| `Misses_are_limited_to_300_per_minute_per_ipv4_24` | 404 × 301 | F-03 / BE-031 |
| `Head_misses_count_like_get_misses` | 404 × 31 | F-03 / BE-031 |
| `Three_character_path_returns_404_without_lookup_even_if_a_row_exists` | `GET /bmn` → 302 to the live row's destination | F-08 / BE-031 |

QA verdict: **PASS for BE-001, BE-030 and BE-031 on the uncommitted code as of runs 3 and 4.** Re-run after the lead commits. `TrustedProxyTests` (proxy configured, two visitors behind it get separate partitions) passed both before and after. Before, it passed for the wrong reason: every peer was trusted. After, it is the positive guard for BE-030.

### Still failing after the backend changes (49)

| Test | Actual | Classification |
|---|---|---|
| `Matrix_enrol_only_token_is_refused` (24 real cases) | 200/201 on user routes; 404 on owner-link routes, because it authenticated and the link is not "its own". **`POST /api/api-keys` → 200 with a new key**; `GET /api/auth/me` → 200 | **New defect AZ-01** (proposed S2). Spec §7.4: the enrol-only token is "valid only for `/totp/enroll` and `/totp/confirm`". Today it passes `RequireAuthorization()` and the `Interactive` policy, because it carries `sub` and `sstamp` and nothing checks the `enrol_only` claim. An admin who has only the password (no second factor) can mint an API key and use every user-level route. It cannot reach `/api/admin/*`, because it has no role claim. Owner: backend (Identity / `Program.cs` policies). |
| `Head_does_not_consume_a_click` | After 3 HEADs on a link with `MaxClicks=1`, the GET got the fallback | **New defect RD-01** (proposed S3). §7.3 says "HEAD never counts". `ResolutionEndpoints` sends HEAD through `ResolveAsync` → `PassAsync` → `TryConsumeClickAsync`, so link checkers and preview fetchers burn click-limited links. |
| `Non_active_link_returns_410_static_page_per_spec` (Disabled, Deleted, Expired) | 302 | **SpecGap §7.3** (known: `AccessGate.cs` comment calls it a deliberate simplification). Needs a product decision before Phase 1 serves content links. The pinned current behaviour (`Non_active_link_current_behaviour_…`, Trait Pins=current) passes: 302 to the fallback or `{Frontend}/expired`, never to the destination. |
| `Active_link_cache_control_is_private_max_age_0_per_spec` | `Cache-Control: no-store` | **F-09** (S4), not part of BE-031. |
| `Matrix_admin_can_use_owner_routes` (20) | 404: Admin JWT on another user's link or folder routes | **SpecGap §7.4** ("owner … or Admin"). Admin today uses `/api/admin/links/{code}` instead. Needs a decision: implement it, or amend the spec row. |

No failure in any run is a harness problem. Run 2 had one transient failure, explained next.

## 3. Harness notes and the one flake found

- **Window-boundary flake (fixed in the test, run 2):** BE-031's `InMemoryRedirectRateLimiter` uses one-minute windows aligned to the clock (`unix seconds / 60`). In run 2 a 301-request burst crossed `:00` and passed for the wrong reason. Bursts now wait until the app clock is before `:45` (`AlignToWindowAsync`). Note for the backend: aligned fixed windows let a client make 2× the limit across a boundary. That is acceptable for "per minute", but should be stated.
- **Coverage gate:** BE-031 changed the catch-all regex text from `{2,63}` to `{3,63}`. That tripped `Every_route_has_an_oracle_entry` in run 3's first pass; the key is now normalised to `/{slug:redirect}`. The gate also fails when a matrix entry points at a route that no longer exists.
- Tests share one `WebApplicationFactory` (collection fixture). Collection parallelism is off (`xunit.runner.json`). Every test uses its own random addresses (198.18.0.0/15, 2001:db8::/32), so rate-limit partitions never overlap between tests.

## 4. Stubs and overrides in the harness (each unavoidable locally)

| # | What | Why | Where |
|---|---|---|---|
| 1 | Coordinator: named HttpClients `CoordinatorLeaseClient` and `CreationGate` use `FakeCoordinatorHandler` (`/v1/status` returns the test key's fingerprint; `/v1/leases` returns a 100k block at length 10 from a random start below 1 % of 62^10) | No coordinator runs locally (port 5085 refused) | `Harness/TestInfrastructure.cs`, `Harness/ZaaglApiFactory.cs` |
| 2 | Valkey: `IDistributedCache` replaced by the in-memory distributed cache | No Valkey (6379 refused). `/health/ready` is not exercised. | factory |
| 3 | `TimeProvider` replaced by `OffsetClock` (wall clock + an offset that only moves forward) | Expiry tests without sleeping. The offset accumulates about 3 min per run, well inside the 15-min JWT lifetime. | factory |
| 4 | Hosted services: only `CreationGatePoller`, `LinkCacheInvalidationListener` and `ClickWriterService` are kept | The others sweep the whole shared DB (purge, safety re-check, unconfirmed-account cleanup, email dispatch and digest, partition DDL, guest-quota cleanup, admin bootstrap, migrations) and would mutate other agents' rows (cf. F-01) | factory |
| 5 | Remote IP: `RemoteIpStartupFilter` sets `Connection.RemoteIpAddress` from `X-Test-Remote-Ip` before the app pipeline, then strips the header | TestServer has no TCP peer | `Harness/TestInfrastructure.cs` |
| 6 | Config via `UseSetting`: env `Testing`, own `Jwt:Key` and `ShortLinks:ScrambleKey`, `Hosts:Short=zaa.test`, `Hosts:Api=api.zaa.test`, `Captcha:Enabled=false`, `Admin:AllowedNetworks=[10.200.0.0/16]` (so "admin outside network" is testable) | | factory |
| 7 | Plan Starter: direct insert into `billing.subscriptions` for the test's own new user | The only product path is a signed Stripe webhook | `Harness/TestPrincipals.cs` |
| 8 | Admin role: direct update of the test's own new user | The only product path is `AdminBootstrap` at startup (global) | `Harness/TestPrincipals.cs` |

Everything else goes through product code. Users: `IdentityService.RegisterAsync` → `ConfirmEmailAsync` → `LoginAsync`. Admin: TOTP enrol and confirm (an independent RFC 6238 implementation in the test). API keys: `CreateApiKeyAsync` with the chosen scopes. Links: `LinkService.CreateAsync`, or `POST /api/links` over HTTP.

**Data left in the shared DB:** users `qa-api-*@example.test`, their subscriptions, links on `example.com/qa/*`, folders `qa-authz-*`, API keys `qa-*`, queued confirm-email outbox rows, and guest links from the anonymous `POST /api/links` matrix case. The one direct 3-character row is deleted by its test. No test deletes rows it did not create.

## 5. Product seams requested from the backend (not done by QA; `src/` untouched)

1. **Background jobs switch:** a config flag per hosted service, or one `Jobs:Enabled`. The harness removes them by implementation type name, which breaks silently when a job is renamed.
2. **TimeProvider everywhere on the create and auth paths:** `LinkService` (expiry-in-future check, monthly cap), `TokenService` and `IdentityService` still read `DateTime.UtcNow`. Expiry and token-lifetime tests cannot move time consistently until they do.
3. **Code-lease abstraction:** an `ICodeLeaseSource` (or similar) in front of `CoordinatorLeaseClient` and `CreationGate`, so tests do not depend on the coordinator's HTTP wire shape.
4. **Redirect lookup counter:** a metric or counter for `redirects` lookups, so "rejected before any I/O" (§7.3) can be asserted directly, not only through the live-row oracle.
5. **Plan test seam:** an internal way to set a plan without Stripe, or a test-signing helper for the webhook.
6. **Redirect limiter:** `IRedirectRateLimiter` exists now. Registering it so tests can swap it is enough; nothing more is needed.

## 6. Not done this round

| Item | Status | Reason |
|---|---|---|
| Full suite `dotnet test Zaagl.sln` | **NOT_TESTED** | The lead later said "build and test only your project" in the shared checkout, because other agents are building there. Run it once after the lead commits. Known flake: `AdminApiTests.Owner_links_disabled_and_restored_as_user_disabled_kind_only` (SECURITY_AND_QA.md §1.3). |
| Authz matrix: routes with `Execute: false` | NOT_TESTED, with a reason on each entry in `AuthzOracle.Rules`: `/api/auth/*` (register, login, refresh, logout, logout-everywhere, TOTP: the enrol-only token must be **accepted** there), `/api/me` DELETE and export, billing, webhooks (unsigned → 401), `/api/reports`, `/hubs/analytics`, `/api/admin/allocator`, `/api/admin/declare-restore` (global alias freeze: never against the shared DB) | Destructive, global, or needs Stripe or the coordinator |
| Admin positive cases for admin writes | NOT_TESTED by design | Would mutate global admin state in the shared DB. Needs QA-002's per-class DB. |
| Principal "guest" (owner-less link) on management routes | TODO | §7.4: every management route returns 404 for guest links |
| QA-008 extras: cache invalidation latency, CSP/nosniff on non-302 pages (F-06) | TODO | The F-06 page does not exist until the 410 SpecGap is decided |
| QA-009: unlock limit per /64 | Covered by the QA-011 /64 unlock test | |

## 7. Files

`tests/Zaagl.Api.Tests/`: `Zaagl.Api.Tests.csproj` (added to `Zaagl.sln`), `xunit.runner.json`, `Harness/ZaaglApiFactory.cs`, `Harness/TestInfrastructure.cs`, `Harness/TestPrincipals.cs`, `HarnessSmokeTests.cs`, `RedirectEndpointTests.cs`, `AccessGateTests.cs`, `RateLimitAndClientIpTests.cs`, `AuthorizationMatrixTests.cs`.

Find tests by trait: `dotnet test tests/Zaagl.Api.Tests --filter "Defect=BE-001"` (also `BE-030`, `BE-031`, `F-02`, `F-03`, `F-08`, `F-09`, `AZ-01`, `RD-01`), `--filter "Area=QA-010"`. Since round 2 (§8): `--filter "Decision=PD-39"` (also `PD-40`, `PD-42`, `PD-43`, `PD-44`, `PD-47`), `--filter "Pending=PD-41"`, `--filter "Rework=R1"`. The traits `SpecGap=7.3-non-active-410`, `SpecGap=7.4-admin-owner-routes` and `Pins=current` no longer exist.

## 8. Round 2 (2026-11-09): tests aligned to the approved decisions and the Head review

Same checkout and branch, still uncommitted; QA edited only `tests/Zaagl.Api.Tests/**` and this file. The backend agent was editing `src/` during the round, so results describe the working tree at the time of each run.

### 8.1 What changed in the tests

| Item | Change | Files |
|---|---|---|
| R1 / spec §7.4 | `GET /api/auth/me`, `GET /api/billing/me`, `POST /api/auth/logout-everywhere`: `AnyAuthenticated` → `JwtOnly` (the `AnyAuthenticated` access kind is removed). `JwtOnly` now expects **401** (not just 401/403) for API keys. `/api/billing/me` is now executed. | `AuthorizationMatrixTests.cs` |
| PD-40 | `Matrix_admin_can_use_owner_routes` (SpecGap 7.4) → `Matrix_admin_gets_404_on_other_users_owner_routes` (Trait `Decision=PD-40`): admin JWT, in or outside the admin network, gets 404 on another user's link/folder/analytics routes, like any non-owner. Admins act through `/api/admin/*` (Admin rows of the matrix). | `AuthorizationMatrixTests.cs` |
| PD-39 + PD-42 (§7.3) | Pinned "302 to fallback or `/expired`" test and the SpecGap 410 test replaced by: expired with fallback → 302 to the fallback (GET and HEAD); expired without fallback, disabled (±fallback), deleted (±fallback) → 410 HTML page, body never contains destination or fallback; purgeable (direct `purge_after` write on the test's own row) → 404; unknown code → 404; HEAD same status, empty body; headers `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`, `nosniff`, `Referrer-Policy: no-referrer`, no `Set-Cookie`, no `Location`. F-09 test unchanged. | `RedirectEndpointTests.cs` (shared `StaticPage` helper) |
| PD-43 | Used-up click limit → 410 page mentioning "click limit", static-page headers, never the fallback (with and without one); HEAD 410 without body; counter stays at the limit. The 50-parallel exactness theory now expects N × 302 to the destination and (50−N) × 410, zero fallbacks. | `AccessGateTests.cs` |
| PD-47 / RD-01 | HEAD on a click-limited link (clicks still left) → 410, no `Location`, no body, `ClicksUsed` stays 0, the next GET still reaches the destination. Ordinary link: HEAD = 302 + `Location`, no body. | `AccessGateTests.cs` |
| PD-44 | New host `FallbackChecksApiFactory` (blocked host via `Safety:DenyRedirectorHosts`, fake Web Risk flagging a marker URL; nothing global written to the DB). Blocked/unsafe fallback refused with 400 on create and on `PUT`; the old fallback survives a refused update; a safe fallback is accepted; oracle test proves the same URLs are refused as destinations. | `FallbackUrlChecksTests.cs` (new) |
| PD-41 (Trait `Pending=PD-41`) | New host `ExternalLoginApiFactory` replaces only the `External` cookie scheme with a fake handler, so the real callback, `ExternalSignInUserAsync` and token issuing run. Tests: callback `Location` carries no token (`token=`, `refresh=`, `access_token`, `eyJ`); `POST /api/auth/external/exchange {code}` returns tokens once, second use 4xx; `POST /api/auth/hub-ticket` 401 for anonymous and API key, 200 for a JWT; ticket `expiresAt` 30–90 s after the app clock; negotiate with `?ticket=` 200, WebSocket connect consumes it, a later negotiate with it 401; forged ticket 401. Written first from the decision wording, then aligned to the routes the backend added (`?code=` on the callback; ticket peeked on negotiate, consumed on connect). | `Pd41TokenExposureTests.cs` (new) |
| Execution coverage (Head: R1, AZ-01 not executed) | New `AuthRoutesTests`: register/resend/forgot-password 202 with identical bodies for new and existing addresses, no token echo; login → refresh (rotates) → logout → refresh refused; logout-everywhere 401 for anonymous, API key, enrol-only token (and the key attempt did not log the owner out); logout-everywhere with a JWT → 204 and the refresh token is dead; the three R1 routes 401 to an all-scope key; **enrol-only token accepted on `/totp/enroll` and `/totp/confirm`**, still 401 on `/api/auth/me`, then admin login with the TOTP code succeeds; TOTP routes 401 for anonymous and keys; hub negotiate: anonymous 401, JWT 200, `stats:read` key 200, key without it 403, enrol-only 401, garbage bearer 401, short host 421. Matrix notes point at these tests. | `AuthRoutesTests.cs` (new) |
| Coverage gate | New routes `POST /api/auth/external/exchange` (Anyone, executed in PD-41 tests) and `POST /api/auth/hub-ticket` (`JwtOnly`, executed in the matrix) added after the gate failed in run 6. | `AuthorizationMatrixTests.cs` |
| Addendum §17 note 5 interim | Between runs 5 and 7 the click-limit-without-fallback test was relaxed to an "OPEN" invariant; superseded by PD-43 (strict 410 test above). | `AccessGateTests.cs` |

### 8.2 Results

| Run | Total | Passed | Failed | Note |
|---|---|---|---|---|
| 5 | 537 | 532 | 5 | 4 × PD-41 (callback still put `#token=…&refresh=…` in `Location`; `/api/auth/hub-ticket` 404) and `Click_limit_without_fallback_goes_to_the_expired_page` (got 410: backend had already moved this case) |
| 6 | 537 | 532 | 5 | 4 × PD-41 (same) and `Every_route_has_an_oracle_entry` (backend had just added `external/exchange` and `hub-ticket`) |
| 7 | 555 | 555 | **0** | after PD-43/44/47 tests, PD-41 alignment and the two oracle entries |
| 8 | 555 | 555 | **0** | repeat |
| 9 | 555 | 555 | **0** | repeat, after the coordinator's note that PD-47 was still being finished |

Run 9 by area: matrix 469 (`Matrix_core` 381, BE-001 scope 40 + headline 1, AZ-01 enrol-only 26, PD-40 admin 20, coverage gate 1); `RedirectEndpointTests` 33; `AccessGateTests` 14; `RateLimitAndClientIpTests` 14 + `TrustedProxyTests` 1; `AuthRoutesTests` 11; `Pd41TokenExposureTests` 5; `FallbackUrlChecksTests` 6; harness 2.

### 8.3 Failure classification

| Failure (runs 5–6) | Classification | Status in runs 7–9 |
|---|---|---|
| 4 × `Pd41TokenExposureTests` | Expected until backend lands PD-41 | PASS (backend landed PD-41) |
| `Click_limit_without_fallback_goes_to_the_expired_page` | Test problem: it pinned Phase 0 behaviour that was OPEN (addendum §17.5), then decided by PD-43 | Replaced by the PD-43 test, PASS |
| `Every_route_has_an_oracle_entry` | Test problem (expected, gate working as designed): new PD-41 routes without oracle entries | PASS |

No product defects open in this suite as of run 9. AZ-01, RD-01, F-09, SpecGap §7.3 (now PD-39/42/43/47) and SpecGap §7.4 (now PD-40) all PASS on the uncommitted backend working tree. Re-run after the lead commits; results are on a tree the backend agent was still editing.

### 8.4 Open points and limits

- **PD-44 error code:** the coordinator's brief says `400 destination-blocked` / `destination-unsafe`; the backend answers `fallback-blocked` / `fallback-unsafe` (`LinkService.cs`). The decision names no code, so the tests accept either and require 400 + "blocked"/"unsafe". The architect should fix the code in the addendum; QA then tightens the assertion.
- **PD-41 contract:** the hub ticket is peeked on negotiate and consumed on the WebSocket connect, so long polling and SSE cannot use it (backend note). The tests use WebSockets. The `access_token` query-string JWT path on `/hubs` still exists in `Program.cs` ("remove before Stage 2"); add a test refusing it when it is removed.
- **logout-everywhere:** the old access token is not asserted dead, because the security stamp is cached for 60 s (by design, spec §7.4 "~60s"). Only the refresh token is asserted revoked.
- **PD-47 "without counting" on ordinary links:** not observable through the API (no click counter for unlimited links). NOT_TESTED; it needs the analytics read model or a counter seam.
- **Abuse/safety disabled page** (§7.3 acceptable-use page linking to `Links:ReportPageUrl`), and the 410/404 cache TTLs of addendum §17 note 1: NOT_TESTED this round.
- Two new extra hosts (`ExternalLoginApiFactory`, `FallbackChecksApiFactory`) each start their own copy of the three kept hosted services against the shared DB, like `TrustedProxyFactory`. New data left behind: `qa-api-qa-ext-*@example.test` users (social sign-in), `qa-api-auth-*` users, and one-time codes.
- Full solution `dotnet test` is still NOT_TESTED (lead's instruction while the backend builds).
