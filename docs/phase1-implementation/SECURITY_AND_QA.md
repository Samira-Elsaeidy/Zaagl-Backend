# Phase 1 security and QA

| | |
|---|---|
| Owner | zaagl-qa (independent) |
| Status | PLANNING, awaiting approval. No Phase 1 code exists yet. |
| Source | _drafts/qa.md (whole), written 2026-11-09 by the planning agent named above and kept verbatim below this header |
| Decisions | Numbered PD-nn in [DECISIONS.md](DECISIONS.md). Draft question ids (D-n, Qn, Q-nn) map to PD entries there. |

## Consolidation notes

These are the lead architect's overrides. Where they differ from the draft text below, they win.

- **Independence:** QA writes test plans, test code and evidence. It does not implement product features. Defects go to the owning engineer, and QA retests the fix.
- **Environment:** PostgreSQL was stopped while QA audited, so §0 records the suite as not run. A run earlier on 2026-11-09 gave 274 tests with 272 passing. The two failures were the AdminApiTests flake that §1.3 diagnoses and a partition-bounds bug fixed in commit 1effe01.
- **Phase 0 defects (PD-24, proposed):** F-02 (forwarded headers) becomes BE-030; F-03 (redirect limits, IPv6 /64) becomes BE-031; F-04 (tokens in localStorage) becomes FE-029; F-01 and F-05 (shared test database) become QA-002 and QA-003. All are in Stage 1.
- **Gates:** the stage gates in §4 are the release gates used by IMPLEMENTATION_PLAN.md.
- The draft's open questions are mapped in DECISIONS.md (Q-01 to Q-12).

---

# Zaagl Phase 1: QA and security test strategy (draft)

Owner: independent QA and security engineer (role: `.claude/agents/zaagl-qa.md`). Status: **planning draft, nothing implemented**.
Date: 2026-11-09. Baseline: Zaagl-Backend `main` at `1effe01` (uncommitted: `.claude/`, `docs/phase1-implementation/`, `tools/`), Zaagl-Frontend at `2c70b59`.
Scope: E1.1 snippets (+CLI), E1.2 original-quality media + viewer, E1.3 ephemeral files (5 GB Pro, Burn After Downloading, delete after X days), E1.4 Tauri tray app (Ctrl/Cmd+Shift+Z) and .NET AOT CLI, on the accepted Phase 0 link engine.
Sources read: PRD and tech stack (`Zaagl-Frontend/src/Doc/`), spec v9.4 (§1.2, §4.5, §7.3, §7.4, §7.15, §8, §8.2, §8.3, §10, §11), `docs/TRACEABILITY.md`, `_drafts/phase0-status-snapshot.html`, every file under `tests/`.

## 0. Environment evidence for this session

| Check | Command | Result |
|---|---|---|
| PostgreSQL on 5433 | `C:\Users\pc\tools\pgsql\bin\pg_isready.exe -h localhost -p 5433` | `localhost:5433 - no response` (exit 2). **Backend suite NOT run: BLOCKED.** Not started by QA, per instructions. |
| Docker | `docker --version` | not installed (command not found). All container checks are **BLOCKED** locally. |
| .NET SDK | `Get-Command dotnet` | `C:\Program Files\dotnet\dotnet.exe` present. |
| CI | look for `.github/` in both repos | absent in both. No pipeline exists. |

Every finding below comes from reading the code (file:line). None comes from running the tests. Each finding needs a confirming run once PostgreSQL is up.

---

## 1. Test-estate audit

### 1.1 Inventory

| Project | Files / test attributes (`[Fact]`/`[Theory]`) | Packages | Notes |
|---|---|---|---|
| `tests/Zaagl.Links.Tests` | 17 files, 158 attributes (SlugAndUrl 34, ClientAndAnalytics 23, RoutingAndAccess 14, IdentityService 13, LinkState 12, AbuseControls 11, GuestCreation 10, AccountDeletion 6, EmailJobs 6, AdminApi 5, HostRouting 5, PurgeJob 5, AnalyticsPhase4 4, ChaosScenario 4, RawEventBudget 3, VectorFile 2, VectorGenTrace 1) | xunit 2.9.3, runner 3.1.4, coverlet (`Zaagl.Links.Tests.csproj:11-14`) | References Links, Access, Resolution, Analytics and **Zaagl.Api** (`:26-30`), but never hosts the API. |
| `tests/Zaagl.Coordinator.Tests` | 1 file, 9 attributes | + Npgsql, `Microsoft.AspNetCore.Mvc.Testing` | The only `WebApplicationFactory` use: `StartupCheckTests.cs:188-192` (coordinator only). |
| `tests/Zaagl.Snippets.Tests` | **0 source files** (csproj only) | xunit | Empty shell. |
| `tests/Zaagl.Files.Tests` | **0 source files** (csproj only) | xunit | Empty shell. |
| Media tests | **does not exist** | none | `src/Modules/Media` holds only `.gitkeep` files, like Snippets and Files. |
| Zaagl-Frontend | none | `package.json` scripts are only `dev/build/start/lint`. No Vitest, Jest or Playwright, and no config files. | ESLint is the only automated check. |

Tools the spec requires (§10, §12 step 1) but that are absent: Testcontainers (comments in `StartupCheckTests.cs:15`, `AnalyticsPhase4Tests.cs:10`, `IdentityServiceTests.cs:13` and `PurgeJobTests.cs:17` say "none set up"), a `Zaagl.Api.Tests` WAF harness, `Zaagl.Architecture.Tests` (NetArchTest), `Zaagl.Scenarios`, `Zaagl.Chaos`, Stryker.NET, BenchmarkDotNet (only `.gitignore:61` mentions it), `tools/traceability-check` (deferred, `TRACEABILITY.md:9-10`) and CI. Banned-API analyzer RS0030 runs as warnings, not errors (`Directory.Build.props:15-18`).

### 1.2 How the tests reach PostgreSQL

- The same connection string is **hard-coded 12 times**: `Host=localhost;Port=5433;Database=zaagl;Username=zaagl;Password=zaagl_dev`. Locations: `AbuseControlsTests.cs:15`, `AccountDeletionTests.cs:26`, `AdminApiTests.cs:17`, `AnalyticsPhase4Tests.cs:15`, `ChaosScenarioTests.cs:26`, `EmailJobsTests.cs:20`, `GuestCreationTests.cs:16`, `IdentityServiceTests.cs:18`, `PurgeJobTests.cs:21`, `StartupCheckTests.cs:30` and `:192`. There is no environment-variable override, so CI cannot point the tests anywhere else.
- It is the **same database the dev API uses**. `docker-compose.yml:15` maps 5433 to the compose Postgres, and `README.md:24` says the dev API migrates at startup. The tests never migrate. They assume a dev API has already run. The one exception is `StartupCheckTests.cs:37`, which calls `AllocSchema.EnsureCreatedAsync`.
- Nothing is cleaned up. Test rows (links on `example.com`, `bad.example` and similar, users, outbox rows, blocked aliases, abuse evidence) build up across runs in the dev database. Destructive statements touch shared rows: `TRUNCATE alloc.*` plus a pool reset (`StartupCheckTests.cs:51-52`, which wipes the dev allocator state and must never run beside a live coordinator, as `:15-19` itself admits), `AliasFreezes.ExecuteDeleteAsync()` with no filter (`AdminApiTests.cs:130,147`, which deletes every freeze row, including real ones), and a global demotion of the other admins (`AccountDeletionTests.cs:121,130`).
- Parallelism: xUnit v2 by default puts each test class in its own collection and runs collections in parallel. There is no `xunit.runner.json` and no `[Collection]` attribute anywhere. `dotnet test Zaagl.sln` can also run both test assemblies at the same time.

### 1.3 Flaky test diagnosis: `AdminApiTests.Owner_links_disabled_and_restored_as_user_disabled_kind_only`

**Root cause (from code; needs a confirming run): a cross-class race with `AbuseControlsTests.Recheck_disables_a_link_flagged_unsafe`, which runs the production `SafetyRecheckJob` over the whole shared database with a reputation checker that marks every URL unsafe.**

1. `AdminApiTests.cs:73` inserts `normal` (owner = new Guid, `DisabledKind = null`, `SafetyCheckedAt = null`, `IsCustom = true`, created now). `:74` inserts a second link. `:76-79` runs `UPDATE … WHERE owner = @owner AND (disabled_kind IS NULL OR = 'user-disabled')` and asserts **exactly 1** row changed. `:83` asserts `normal` is `user-disabled`.
2. In another class running in parallel, `AbuseControlsTests.cs:172-182` calls `MakeJob(new AlwaysUnsafe())` (`:148-151`, `:158-169`), then `RunOnceAsync`.
3. `SafetyRecheckJob.RunOnceAsync` (`src/Modules/Links/Application/AbuseControls.cs:164-173`) claims **any** 200 links where `deleted_at IS NULL AND disabled_at IS NULL AND (safety_checked_at IS NULL OR … OR created_at > now-30d)`. No owner or test filter applies, and `ORDER BY safety_checked_at NULLS FIRST` sets no order among the null rows. A link `AdminApiTests` has just inserted qualifies on two counts (`safety_checked_at` is null and it was created in the last 30 days).
4. The claimed links pass through `TakedownService.TakeDownAsync(link, Safety, "Flagged by re-check: MALWARE")` (`AbuseControls.cs:194-197`). That sets `disabled_kind = 'safety'` and, because the link is custom, also writes `blocked_aliases` and `abuse_evidence` rows.
5. If this lands between `:73` and `:76`, the update matches 0 rows and the test fails at `:79`. If it lands between `:76` and `:83`, the takedown overwrites `user-disabled` with `safety` (per spec §7.15) and the test fails at `:83`. When the class runs alone, or runs before or after `AbuseControlsTests` rather than alongside it, the job never sees the fresh link. That explains why the failure shows up **only in full-suite runs**.

The same exposure applies to `ChaosScenarioTests.Admin_disable_with_links_then_enable_with_restore_…` (`ChaosScenarioTests.cs:94-104,117`), `AdminApiTests.Bulk_takedown_…` (the `other` link, `:92,107`) and `AbuseControlsTests.Recheck_leaves_a_safe_link_untouched…` / `Recheck_resets_…` (`:185-211`). The last two can also flake on their own: their link may not be among the 200 that the unordered `LIMIT` picks once more than 200 eligible rows have built up in the dev database.

**Forensic confirmation (read-only, run when the DB is up):** `SELECT slug, reason, created_at FROM links.abuse_evidence WHERE slug LIKE 'ad%' AND reason LIKE 'Flagged by re-check%' ORDER BY created_at DESC;`. Rows here mean `AdminApiTests` links (prefix `ad`, `AdminApiTests.cs:55`) were taken down by the re-check job. A reproduction is in QA-001.

**Why stamping `SafetyCheckedAt` would not fix it:** the job's predicate re-selects every link created in the last 30 days on every run (`AbuseControls.cs:169`). Isolation is the only fix: a per-class database, or a non-parallel collection for global-sweep jobs (QA-002, QA-003). Separately, that predicate departs from spec §7.15, which says candidates must *also* have `safety_checked_at` null or older than 6 h. As written, every recent link is looked up again on every run, which spends the Web Risk budget. Logged as candidate defect F-10.

### 1.4 Other estate findings (candidate defects, to be confirmed by a run)

| ID | Finding | Evidence | Phase 1 impact | Proposed severity |
|---|---|---|---|---|
| F-01 | Shared-DB race described in §1.3 | above | Phase 1 adds global sweeps (orphan cleanup, expiry, burn TTL) that would multiply flakes | S3 (test) |
| F-02 | Forwarded headers trusted from **any** source: `KnownIPNetworks.Clear(); KnownProxies.Clear();` | `src/Zaagl.Api/Program.cs:47-53` | A spoofed `X-Forwarded-For` defeats every IP limiter (unlock, auth, guest and future upload limits). Violates MUST NOT #29. | S1 if confirmed |
| F-03 | Rate limiting partitions on the full `RemoteIpAddress` (IPv6 not cut to /64), and the redirect route has **no** limiter (no 300/min, no tiered miss limits) | `Program.cs:73,82-85`; `ResolutionEndpoints.cs:16-35` has no `RequireRateLimiting` | Enumeration of content IDs on `/f`, `/s`, `/i`. Violates MUST NOT #24 and §7.3. | S2 |
| F-04 | Web app keeps access and refresh tokens in `localStorage` | `Zaagl-Frontend/src/lib/token-store.ts:11,19,35-36` | Any Phase 1 XSS in the `app.zaa.gl` origin (Markdown, SVG or PDF viewer) can steal long-lived refresh tokens | S1 gate for Stage 2 |
| F-05 | Tests copy production queries instead of calling the production code ("LinkService needs the full module graph") | `AdminApiTests.cs:45-47,76-78`; `ChaosScenarioTests.cs:80-85,101-112` | These tests prove a copy of the logic. A regression in `AdminSetOwnerLinksDisabledAsync` would still pass. | S3 |
| F-06 | Short-host responses carry no CSP, `nosniff` or `Referrer-Policy` (no header code anywhere in `src`). The preview HTML is served as `200 text/html` on the short host. | `ResolutionEndpoints.cs:31,53-71`; grep for `Content-Security-Policy` returns 0 hits | Spec §7.3 and §4.5 rule 6. Phase 1 serves user content near this origin. | S2 |
| F-07 | CORS with `AllowCredentials` on the API host, though spec §1.2 says "no CORS" | `Program.cs:63-67,236` | Widens the cross-origin attack surface for content viewers | S3 (decision) |
| F-08 | The redirect regex `{2,63}` accepts 3-character segments, and the lookup does I/O for them. Spec §7.3 says reject 1-3 characters before any I/O. | `ResolutionEndpoints.cs:16` vs `SlugRules.cs:11` (`{3,63}`) | The `/f`, `/s`, `/i` namespace and enumeration cost | S3 |
| F-09 | `Cache-Control: no-store` where spec says `private, max-age=0` | `ResolutionEndpoints.cs:23` | Cosmetic | S4 |
| F-10 | Re-check predicate departs from spec (see §1.3) | `AbuseControls.cs:169` | Web Risk budget burn | S3 |

### 1.5 Phase 0 behaviours Phase 1 depends on that lack automated tests

| Behaviour Phase 1 reuses | Current coverage | Gap | Task |
|---|---|---|---|
| Link creation (`LinkService.CreateAsync`, namespace, alias lock, quota, idempotency) for content links | **None.** Tests say constructing LinkService "needs a live coordinator" (`ChaosScenarioTests.cs:83-84`) | No test creates a link through the product path | QA-007 |
| Redirect / resolve endpoint (status, headers, 302-only, no cookies, HEAD, reserved and short segments, host routing) | `HostRoutingTests` (pure function) and `LinkStateTests` (pure) | No HTTP test of `ResolutionEndpoints` | QA-008 |
| AccessGate (password, expiry, click limit) and `POST /api/unlock/{slug}` | `AccessGateTests` (pure, `RoutingAndAccessTests.cs:83-120`) | No HTTP unlock test. Atomic click-limit counting, the unlock rate limit and the fallback URL are untested. Phase 1 must gate **bytes**, not just a redirect. | QA-009 |
| Auth (JWT, refresh rotation, TOTP, API keys) | `IdentityServiceTests` (service level, 13) | No endpoint auth test and no §7.4 generated route × principal matrix. API-key scopes are untested at HTTP level. | QA-010 |
| Rate limits (`auth`, `link-write`, `unlock`, guest) | `GuestRateLimiterTests` (in-memory) | Named ASP.NET policies untested. F-02 and F-03. | QA-011 |
| Takedown, cache invalidation (`PgNotify`), reports | `TakedownService` direct; reports lifecycle | No NOTIFY or invalidation test. Admin HTTP untested (`TRACEABILITY.md:88`). | QA-012 |
| Purge / expiry jobs | `PurgeJobTests` (5) | No `FakeTimeProvider`. Global sweep with no isolation. | QA-031 |
| Account deletion / export | `AccountDeletionTests` (6) | Content bytes not covered (they do not exist yet) | QA-041 |
| Redirect latency (p95 5 ms hit / 15 ms miss, §7.3; PRD < 50 ms) | none ("not yet load-tested", snapshot E0.1) | No baseline, so Phase 1 regressions cannot be detected | QA-013 |

---

## 2. Phase 1 test strategy

**Principles.** Test against real PostgreSQL, never EF InMemory. Every test owns its data and its database. Every security property has a negative test that can fail. Failpoints are proven hit with counters (§8.3). `TimeProvider` / `FakeTimeProvider` drives all expiry. The spec, not the implementation, is the oracle. Each result is recorded as PASS, FAIL, BLOCKED or NOT_TESTED with evidence.

### 2.1 Layers

| Layer | Scope | Tooling | Where it runs |
|---|---|---|---|
| Unit | Content-type sniffing (magic bytes), filename sanitising, storage-key factory, Markdown sanitiser policy, signed-token mint/verify, burn state machine, quota arithmetic, SVG policy, size and dimension limits, CLI argument parsing | xUnit, FsCheck (property tests for filename and key), Stryker on the security-critical classes | local + CI |
| Integration (real PG) | Upload session → finalize → scan → publish; burn claim; expiry and orphan jobs; quota counting under concurrency; takedown of content | xUnit + per-class DB (§2.3); `IFileStorage` on a temp dir; `IMalwareScanner` fake **and** real clamd (CI) | local (if PG up) + CI |
| API contract | Every Phase 1 route: request and response schema, problem-details codes, headers, OpenAPI snapshot diff, generated authorization matrix extended with snippet, file and media routes × principals (§7.4) | `WebApplicationFactory<Program>` (new `Zaagl.Api.Tests`), Verify snapshots of `/openapi/v1.json` | local + CI |
| Architecture | Module boundaries (Contracts only); redirects use only the `redirects` pool; content routes never mapped on the short host's cookie path; storage paths only via `IStorageKeyFactory`; no `File.Open` with request data | NetArchTest (`Zaagl.Architecture.Tests`) | CI |
| E2E | Browser flows on the real hosts layout (app, api, short host, content origin): snippet create/view/raw/diff, media viewer (bg canvas, loupe), file download page, burn, password gate, XSS corpus execution checks, CSP violation reporting | Playwright (in Zaagl-Frontend, new) against compose or staging; `page.on('dialog')` and CSP report collectors as XSS oracles | CI nightly + staging |
| Desktop | Tauri: Rust unit tests for commands; WebDriver via `tauri-driver` on Windows; manual matrix on macOS (hotkey, permissions, notarisation) | cargo test, tauri-driver, manual checklist with screenshots | Windows runner + manual macOS |
| CLI | Arg parsing, token sources, stdin piping, exit codes, proxy and TLS, AOT smoke per RID | xUnit + process-level black-box tests of the published AOT binary | CI matrix win-x64, linux-x64, osx-arm64 |
| Performance / load | 5 GB resumable upload; 100-500 concurrent downloads through X-Accel-Redirect; redirect latency regression; upload-endpoint memory stays flat | k6 (HTTP, tus via xk6 or a scripted PATCH loop), BenchmarkDotNet for the redirect hot path, OTel histograms (§8.3 profile) | staging-shaped host only (not shared CI); PR CI = structural checks + relative benchmark |
| Recovery | Interrupted upload or resume; API killed mid-PATCH; worker killed mid-scan; clamd down; disk full in temp and in objects; DB down at finalize; burn claim crash between claim and stream | Failpoints (§8.3) at named Phase 1 points; fault-injecting `IFileStorage` (ENOSPC, partial write); compose chaos (BLOCKED locally) | CI (in-process) + staging (container) |
| Security / exploratory | Polyglots, sanitizer bypass, IDOR fuzzing, path traversal, header injection, SSRF, preview-bot behaviour | Corpus-driven xUnit + Playwright; Burp/ZAP session on staging; manual review of headers on real hosts | Stage 6 |

### 2.2 Required test seams (request to engineering at Stage 1)

`IFileStorage` (Save/OpenRead/Delete, plus a `Stat` for size), `IStorageKeyFactory` (server-generated keys only), `IMalwareScanner` (verdicts Clean/Infected/Error/Timeout, where **Error and Timeout are not Clean**), `IContentTypeDetector`, `IDownloadTokenService` (sign/verify with `TimeProvider`), `IImageProbe` (dimensions and frames read **without** decoding), and failpoints: `upload.after-temp-write`, `upload.before-finalize-commit`, `scan.after-verdict-before-move`, `publish.after-move-before-commit`, `burn.after-claim-before-stream`, `burn.after-stream-before-delete`, `purge.after-row-delete-before-blob-delete`. All of these follow the §8.3 rule: the startup check rejects non-no-op failpoints outside `Testing`.

### 2.3 Test-database isolation (fixes F-01, prerequisite for everything)

1. Connection comes from `ZAAGL_TEST_PG` (admin connection to any PostgreSQL 18). The default stays `localhost:5433` for developers. CI uses a service container.
2. An assembly fixture creates `zaagl_tpl_<migrationsHash>` once by running the real migrator (`Zaagl.Api -- migrate`, as `.claude/settings.json.bak:14` shows) against it. Each test class then gets `CREATE DATABASE zaagl_t_<runId>_<class> TEMPLATE zaagl_tpl_…`, about 100 ms, and it is dropped on dispose. A crashed run's leftovers are swept by `runId` age.
3. Classes that run **global sweeps** (re-check, purge, orphan reconciler, burn TTL, outbox dispatcher, alloc wipes) must own a database. Until then they go in `[Collection("GlobalSweeps", DisableParallelization = true)]`.
4. The tests never touch the dev `zaagl` database. A guard in the fixture refuses a database named `zaagl`.
5. Testcontainers (Postgres, clamd, Nginx) is used **in CI only**. Locally, without Docker, container-dependent tests are skipped with an explicit `BLOCKED: no docker` reason, never silently passed. A traceability check counts skipped tests as not covered.
6. Storage isolation: a per-test temp root (`%TEMP%/zaagl-qa/<runId>/<test>`) and an assertion after each test that nothing is left in `temp/` or `quarantine/` unless the test expects it.

### 2.4 Performance targets to verify (proposed; confirm with product)

Redirect p95 ≤ 5 ms hit and ≤ 15 ms miss (server-side, §8.3 profile), no regression > 10 % vs the Stage 1 baseline. Snippet view p95 < 150 ms (server). Upload throughput ≥ 80 % of link bandwidth on the file host. Resume after a cut loses at most one chunk. The API process's working set stays roughly constant (±50 MB) during a 5 GB upload, because uploads stream rather than buffer. 200 concurrent downloads with flat API CPU (Nginx streams via X-Accel-Redirect). Burn claim p99 < 50 ms under 50 concurrent claimers.

### 2.5 Proposed Phase 1 scenario rows (extension of spec §8.2; each becomes a traceability row)

| Event | Required behaviour |
|---|---|
| 50 parallel downloads of a Burn-After-Downloading file | Exactly one full `200`. The rest get `410`. The object is deleted after the stream ends. |
| Download interrupted at 50 %, client resumes with `Range` within token TTL | Same claimant resumes. Any other client gets `410`. After TTL the object is deleted. |
| Slackbot / WhatsApp / Outlook SafeLinks fetch the landing page of a burn file | Nothing is consumed. The OG tags hold no private filename or bytes. |
| `HEAD` and `Range: bytes=0-0` on a burn file | Nothing is consumed. No partial content goes to a non-claimant. |
| API killed at `burn.after-claim-before-stream` | No second download is possible. The object is purged by the reconciler. |
| API killed mid-PATCH of a 5 GB tus upload, restarted | Client resumes from the server offset. Final SHA-256 equals the source. |
| Worker killed at `scan.after-verdict-before-move` | The file stays unavailable and is re-scanned on restart. It is never published unscanned. |
| clamd down for 1 h during uploads | Uploads accepted into temp, or refused (decision). None are downloadable until scanned. An alert fires. |
| Temp volume reaches 100 % | New sessions are refused with a stable error. Existing published files keep downloading. PostgreSQL is unaffected (separate volume). |
| PostgreSQL unavailable at finalize | No object is moved to `objects/` without a committed row. The session is retryable. |
| Crash at `purge.after-row-delete-before-blob-delete` | The orphan reconciler deletes the blob within its period. |
| File reaches `expires_at` while its landing page is cached | Next hit returns `410`. The bytes are no longer served via X-Accel. |
| Owner disabled by admin with `disableLinks` | Content links return `410`. Downloads stop on every instance within the R5 bound. |
| Malware found by a later signature update (re-scan) | Content is disabled (`safety`). Downloads stop. Evidence row kept. The ID is never reused. |
| 20 parallel upload sessions whose sum exceeds the quota | Refused atomically. The overshoot is at most the documented bound. |
| Plan downgraded Pro → Free with a 3 GB file stored | Behaviour per decision (kept until expiry or locked). Never silently served beyond the plan's rules. |
| Snippet with 10 MB of a single line / 1M lines | Size cap enforced. The viewer does not hang (virtualised or truncated, with Copy Raw). |
| Content ID enumeration on `/s/`, `/f/`, `/i/` from one /64 | Tiered miss limiter engages. The alert fires. |
| CLI token passed via argv by mistake | Refused with guidance (or warned), never echoed to the terminal or logs. |
| Tray hotkey with an empty or unsupported clipboard | No upload, a clear notification, no crash. |

### 2.6 Corpora (versioned under `tests/corpus/`, each file with SHA-256 and expected verdict)

- `xss/markdown/*.md` (≥ 200 payloads: OWASP cheat sheet, PortSwigger XSS list, mXSS set, Markdig-specific extensions such as autolinks, raw HTML and attributes).
- `svg/*.svg` (script, event handlers, foreignObject, external refs, XXE, billion laughs, SMIL href animation, CSS import).
- `polyglot/*` (GIFAR, PNG+HTML, JPEG+JS comment, PDF+JS, PDF+HTML, ZIP+JPEG, SVG+HTML, MP4+HTML).
- `bombs/*` (pixel-flood PNG/JPEG/WebP/GIF, 42.zip, nested ZIP, deep-recursion PDF).
- `malware/*` generated at test time only (EICAR string built in code, never committed in plain text, so repo scanners stay quiet), plus EICAR inside zip, nested zip and PDF.
- `names/*.txt` (traversal, Unicode confusables, RTL override, reserved device names, CRLF, overlong).
- `media/fidelity/*` (16-bit PNG, CMYK JPEG with ICC profile, animated GIF and WebP, HDR AVIF if supported, screen recordings MP4/WebM/MOV) for byte-identical round trips.

---

## 3. Security and QA matrix

Method key: U = unit, I = integration (real PG), A = API/WAF, E = Playwright E2E, M = manual/exploratory, P = perf, R = recovery/failpoint, C = container (BLOCKED locally). Priority: P0 blocks the stage, P1 blocks release, P2 should, P3 nice.

| # | Threat / requirement | Test cases | Method | Pri |
|---|---|---|---|---|
| S-01 | Content type decided by magic bytes, not by client header or extension | PNG/JPEG/GIF/WebP/MP4/WebM/PDF/ZIP with matching magic → accepted with canonical type. `evil.png` containing HTML → rejected or stored as `application/octet-stream`. A client `Content-Type: image/png` on a PDF is ignored. Empty file. Truncated header. Extension case variants. | U, A | P0 |
| S-02 | Malware (ClamAV) **fails closed** | EICAR as a plain file, inside ZIP, inside nested ZIP, inside a PDF and with a renamed extension → never published (quarantined, 422/`content-infected`). clamd down, timeout or an `Error` verdict → stays pending or rejected, never served. Signature age > 24 h raises an alert. A scan verdict tied to a SHA-256 cannot be reused for different bytes. Download before the scan completes → 409/404. | I, C, R | P0 |
| S-03 | SVG cannot script | `<script>`, `onload=`, `<foreignObject><iframe/html>`, `xlink:href="javascript:"`, `<use href="https://evil/x.svg#a">`, external `<image href>`, CSS `@import`/`url()`, XXE/`<!ENTITY>`, billion laughs, `<animate attributeName=href values=javascript:…>`, `data:` nested SVG. Expected: served only from the content origin with `Content-Security-Policy: sandbox; default-src 'none'; style-src 'unsafe-inline'`, `nosniff`, or rasterised. Never inline in `app.zaa.gl` or `zaa.gl`. Opening the file directly in a top-level tab runs nothing (Playwright dialog and network oracle). | U, A, E | P0 |
| S-04 | Markdown / HTML XSS in snippets | OWASP XSS cheat-sheet corpus plus mXSS (`<noscript>`, `<svg><style>`, namespace confusion), `[x](javascript:alert(1))`, `data:text/html`, `vbscript:`, autolinks, raw HTML blocks, HTML entities and encoded schemes, `<img onerror>`, `<a target=_blank>` without `rel=noopener`, link titles, code-fence info string injection into the Shiki language class, snippet title, filename and language fields, diff view of malicious both sides. The oracle is no dialog, no outbound request and no CSP report of script execution, plus the server-side Ganss HtmlSanitizer output snapshot. The **Raw** endpoint returns `text/plain; charset=utf-8` with `nosniff` and `attachment` or sandbox. | U, A, E | P0 |
| S-05 | Polyglot files | GIFAR, PNG+appended HTML, JPEG with a `<script>` comment, PDF+JS, PDF+HTML, ZIP+JPEG, SVG+HTML, MP4 with HTML in a box. Expected: served with the detected type, `nosniff`, sandbox CSP and on the content origin, so none execute in any Zaagl origin. Image and media viewers load via `<img>`/`<video>` only. | U, E, M | P0 |
| S-06 | MIME sniffing | Every content response has `X-Content-Type-Options: nosniff`. Unknown or unsafe types → `application/octet-stream` + `Content-Disposition: attachment`. `text/html`, `application/xhtml+xml`, `text/xml` and `image/svg+xml` are never served inline from an origin that holds credentials. Charset is always explicit for text. | A, E | P0 |
| S-07 | Separate origin + CSP/sandbox (§4.5 rule 6, §1.2) | User bytes are served only from the content registrable domain (tech stack §09) or with `attachment` plus `CSP: sandbox`. That host never sets a cookie (`Set-Cookie` absent on every response, including errors). `zaa.gl/f/<id>`, `/s/<id>`, `/i/<id>` are landing pages or redirects only. `Cross-Origin-Resource-Policy`, `frame-ancestors` and `Referrer-Policy: no-referrer` are set. Viewer pages on `app.zaa.gl` embed content via a sandboxed iframe or `<img>` from the content origin. The PDF preview (react-pdf) never runs on the app origin with untrusted bytes. Header checks run on the **real** staging hosts. | A, E, M | P0 |
| S-08 | Authentication | Anonymous, expired JWT (never treated as guest, §11 #27), revoked refresh family, API key without the new content scope, API key of a downgraded plan, disabled user, deleted user. Each against create, list, get-metadata, delete and download-private. | A | P0 |
| S-09 | Authorization / IDOR | Generated matrix: every snippet, file and media route × {anonymous, guest, other user, owner JWT, owner API key per scope, admin no-TOTP, admin TOTP, admin outside network}. Other users get **404, not 403** (§7.4). Sequential and UUIDv7-adjacent ID guessing. Upload-session hijack (PATCH another user's tus session). Thumbnail and preview URLs authorized like originals. Metadata fields (owner email, storage key) never leak in public responses. | A, I | P0 |
| S-10 | Rate limits and upload abuse | Per user and per IP /64 (after F-02 is fixed): sessions/min, concurrent sessions, bytes/day. Slowloris PATCH (1 B/s) times out. 10,000 abandoned sessions are swept and temp disk stays bounded. Snippet creation burst. Enumeration of `/s/xxxx` hits the tiered miss limiter. Download hammering of one file is bounded. Spoofed `X-Forwarded-For` is ignored. | A, P, C | P0 |
| S-11 | Decompression and dimension bombs | 50k×50k PNG (KB-sized), 10k-frame GIF, lossless WebP bomb, JPEG with huge declared dimensions, MP4 with absurd resolution, ZIP bomb (42.zip) uploaded **as a file**. Expected: dimensions are probed from headers before decode and rejected above the limit (e.g. 100 MP). libvips `access=sequential` with a memory cap. ZIPs are **never** decompressed server-side, and any listing preview is bounded by entry count and ratio. The worker survives (no OOM kill) and the API stays responsive. | U, I, P | P0 |
| S-12 | Path traversal and filename abuse | `../../etc/passwd`, `..\..\`, `%2e%2e%2f`, double encoding, fullwidth `．．／`, NUL, `CON`/`NUL`/`COM1`, 1,000-char names, U+202E RTL override (`invoice‮fdp.exe`), CRLF in the filename (header injection into `Content-Disposition`), quotes and semicolons, emoji, Arabic. Expected: the storage key is server-generated (UUIDv7), never derived from the name. The display name is sanitised and emitted RFC 6266 `filename*`. `X-Accel-Redirect` paths are built only from the key. Property test: no input yields a path outside the storage root. | U, A | P0 |
| S-13 | Storage isolation | Nginx internal location returns 404 when requested directly. `storage/temp`, `quarantine`, `thumbnails` and `previews` are never reachable without authorization. No static-file middleware maps the storage root. Two users with identical bytes stay separate objects, or deduplication is proven not to leak existence. Storage directory permissions (non-root container user). | A, M, C | P0 |
| S-14 | Signed download URL / token expiry and replay | Expired by 1 s → rejected (`FakeTimeProvider`). Tampered file ID, expiry, signature or algorithm → rejected. Token for file A used on B → rejected. Replay after burn, delete, takedown or owner disable → rejected. Replay within TTL on non-burn files is allowed and logged. Key rotation keeps old tokens valid until expiry. A token in a query string never reaches logs (§7.4 proxy logging rule). Constant-time compare. | U, A | P0 |
| S-15 | Burn After Downloading races | N=2/10/50 concurrent full GETs → exactly one 200 stream, others 410. Racing `Range: bytes=0-0` probes must not consume the file or yield a second full copy. Ranged resume of an interrupted download within the claim token TTL works and only for the claim holder. A download aborted mid-stream follows the documented policy (Q-03). HEAD never consumes. Preview bots (Slackbot, WhatsApp, Teams, Discord, iMessage, Outlook SafeLinks prefetch) on the landing page never consume: consumption needs an explicit POST/confirm. Both API instances use an atomic `UPDATE … WHERE consumed_at IS NULL RETURNING`. Crash at `burn.after-claim-before-stream` / `after-stream-before-delete` → no second download, and the bytes are eventually deleted. | I, A, R, E | P0 |
| S-16 | Expiry, deletion and orphan cleanup | At `expires_at` the next request gets 410 (cache-hit path too, §8.2). Bytes are deleted within the SLA after purge. Free 7-day deletion (PRD risk register). Orphan reconciler: object without row → deleted after grace; row without object → marked broken, alert. Account deletion removes bytes (§7.14 extension). Takedown keeps the evidence hash and follows the policy for bytes (Q-07). Crash at `purge.after-row-delete-before-blob-delete` → the reconciler catches it. | I, R | P0 |
| S-17 | Quota bypass | Free 25 MB / Pro 5 GB (PRD). The tus `Upload-Length` declares 1 MB but 6 MB is sent → rejected at the boundary. Missing length (deferred length) is refused. 20 parallel sessions each under quota whose sum is over quota → refused atomically. Delete-and-reupload within window rules. Plan downgrade mid-upload. Snippet size cap. Chunked `Transfer-Encoding` without `Content-Length`. Multipart field count. | I, A | P0 |
| S-18 | Phase 0 regression | The full existing suite is green in isolation (§2.3). Redirect latency stays within baseline. Namespace: content IDs never collide with codes or aliases (§4.5 rules 1-6). The reserved list is unchanged (it must never change after launch). Takedown, NOTIFY and R5 bound apply to content links. 302 only. No cookie on the short host. Purge still works. | I, A, P | P0 |
| S-19 | Desktop credential storage | Token held in the OS store (Windows Credential Manager / macOS Keychain), never in plaintext config, webview `localStorage` or logs. Logout wipes it. Scoped device token, revocable from the web. Token not readable by another OS user. The Tauri capability allowlist is minimal (no broad `fs`/`shell`). Webview CSP is set. IPC commands validate input. Updater signature verified (a tampered update is refused). Builds are code-signed (PRD risk register). | M, U | P0 |
| S-20 | Clipboard privacy | The clipboard is read **only** on the hotkey, never polled. Its contents are never logged or sent to telemetry. A confirmation preview is offered for text that looks like a secret (Q-09). Clipboard-manager "concealed" markers are respected (macOS `org.nspasteboard.ConcealedType`, Windows `ExcludeClipboardContentFromMonitorProcessing`). Upload failures do not leave content in a temp folder. The link written back to the clipboard is the only write. | M, E | P1 |
| S-21 | CLI token handling | Token sources: env `ZAAGL_TOKEN`, OS keychain, or `--token-stdin`. **Never** an argv flag (visible in the process list). Config file 0600 / user-only ACL. Redacted in `--verbose`, errors and crash dumps. TLS verification cannot be silently disabled. Proxy env honoured. Exit codes are documented. Stdin piping of 100 MB terminal output streams without OOM. AOT binary smoke per RID. | U, M | P0 |
| S-22 | Access gates on content (password, expiry, click/view limits) reuse Phase 0 | A password-gated file returns no bytes, no thumbnail, no OG image and no filename before unlock. The unlock limit holds per /64 (F-02/F-03). The view limit counts atomically in PG (never in cache, tech stack §10). An expired fallback never leaks content. | A, I | P0 |
| S-23 | Abuse handling for content | Report → admin disable → downloads stop on every instance within the R5 bound. Hash blocklist (re-upload of the same malicious file refused). Bulk takedown by owner covers content. Guest uploads are allowed or refused per Q-05. | I, A | P1 |
| S-24 | Privacy of "original quality" | EXIF GPS, camera serial and XMP author retained or stripped per decision Q-06. If retained, there is an explicit UI warning. The download hash matches the upload hash when "original" is promised. | U, E | P1 |
| S-25 | SSRF / server fetches | No endpoint fetches a user-supplied URL (MUST NOT #28 spirit). SVG external refs and Markdown images are never fetched server-side. OG image generation uses stored bytes only. | U, A | P0 |
| S-26 | Logging hygiene | No snippet bodies, file names of burn files, tokens or IPs in logs (MUST NOT #26). Log injection via filenames with CRLF. | U, M | P1 |

### 3.1 Coverage by epic (matrix rows → tasks)

| Epic | Matrix rows | Tasks | Current status |
|---|---|---|---|
| Cross-cutting (Phase 0 base) | S-08, S-09, S-10, S-18, S-22, S-25, S-26 | QA-001..014, QA-043 | NOT_TESTED (PG down; harness missing) |
| E1.1 Snippets + CLI | S-04, S-06, S-07, S-14 (raw), S-21 | QA-016..020, QA-038, QA-040 | NOT_TESTED (no code: `src/Modules/Snippets` holds only `.gitkeep`) |
| E1.2 Media | S-01, S-03, S-05, S-06, S-07, S-11, S-12, S-13, S-24 | QA-021..024, QA-026..028, QA-048 | NOT_TESTED (no code) |
| E1.3 Files | S-02, S-12..S-17, S-23 | QA-025..035, QA-041, QA-042, QA-047 | NOT_TESTED (no code); clamd and Nginx parts BLOCKED locally |
| E1.4 Desktop + CLI | S-19, S-20, S-21 | QA-036..038 | NOT_TESTED (no code); macOS BLOCKED without hardware or certs |

### 3.2 Test-writing conventions for Phase 1 (to stop F-01/F-05 recurring)

1. Call the product code path. Never copy its query into the test. If construction is hard, that is a design defect against the seam list in §2.2.
2. Every test creates its own principals and data with unique IDs and asserts only on those. Global sweeps run against their own database.
3. Time comes from `FakeTimeProvider` only. Test code never uses `DateTime.UtcNow` (RS0030 is already in place; tests inherit it).
4. Race tests use a start barrier (`TaskCompletionSource`) plus failpoints, run ≥ 20 repetitions, and report counts. A race test that never observed contention is NOT_TESTED, not PASS.
5. Security tests assert the **absence** of an effect through an independent oracle (browser dialog, outbound request, CSP report, file existence), not just a status code.
6. Skipped tests always carry `BLOCKED:` or `NOT_TESTED:` reasons that the traceability checker reads.

---

## 4. QA gates per stage

Result vocabulary for every check: **PASS** (ran, met criteria, evidence attached), **FAIL** (ran, did not meet them; defect filed), **BLOCKED** (could not run, reason and unblock owner named, e.g. "no Docker"), **NOT_TESTED** (not attempted, with a reason). A stage exits only when every P0 check is PASS. BLOCKED or NOT_TESTED on a P0 needs a written waiver from the product owner and the security owner.

| Stage | Entry criteria | Exit criteria | Evidence required |
|---|---|---|---|
| 1 Architecture | Engineering designs for E1.1-E1.4 drafted. Phase 0 accepted. | Threat model reviewed (QA-015). Content-origin decision recorded (Q-01). Seams of §2.2 accepted. Burn semantics decided (Q-03). Isolation harness merged and F-01 fixed (QA-001..003). CI runs the existing suite green 10/10 consecutive runs (QA-004). Phase 0 safety-net tests QA-007..012 PASS. Redirect baseline captured (QA-013). F-02/F-03/F-04 triaged with owners. | Design review notes with QA sign-off; CI run links; baseline report; defect IDs |
| 2 Snippets (E1.1) | Stage 1 exit. Snippet API and viewer feature-complete on a branch. | S-04, S-07 (snippet raw/view), S-08, S-09, S-10 (snippet), S-14 if raw uses tokens, S-18, S-22 PASS. XSS corpus 100 % blocked. OpenAPI snapshot approved. CLI snippet happy path and token rules (S-21) PASS. F-04 resolved or the viewer proven isolated from the token origin. | Test run IDs; Playwright traces and CSP-report logs; sanitizer snapshot; authz matrix report |
| 3 Media (E1.2) | Stage 2 exit. Upload pipeline (temp → scan → objects) exists. | S-01, S-02 (image path), S-03, S-05, S-06, S-07, S-11, S-12, S-13, S-24 PASS. Byte-identical round trip (SHA-256) for all supported formats. Viewer E2E (canvas modes, loupe) PASS. Thumbnail generation survives the bomb corpus. | Corpus results table; hash comparison log; worker memory graph |
| 4 Files (E1.3) | Stage 3 exit. tus endpoint, X-Accel download, burn and expiry implemented. | S-02 (all archives), S-14, S-15 (all race cases at N=50, 20 repetitions, zero double downloads), S-16, S-17, S-10 (upload abuse) PASS. 5 GB upload and resume PASS on staging-shaped host. Recovery suite (QA-035) PASS. | Race-run statistics; k6 reports; failpoint hit counters; orphan reconciler report |
| 5 Desktop / CLI (E1.4) | Stage 4 exit. Signed builds for Windows and macOS. AOT CLI per RID. | S-19, S-20, S-21 PASS. Functional matrix (QA-037) PASS on Win 11 and macOS (current and previous). Hotkey conflict handling. Offline and proxy behaviour. Updater signature negative test. | Matrix with screenshots; `signtool verify` / `spctl -a` output; credential-store inspection notes |
| 6 Final | Stages 2-5 exit. Feature freeze. | Full regression (Phase 0 + 1) green 3 consecutive CI runs. Exploratory pen-test (QA-045) with no open S1/S2. Stryker mutation score ≥ 80 % on security-critical classes (QA-044). Traceability: every Phase 1 requirement row cites a passing test (QA-043). Performance targets (§2.4) PASS with no redirect regression > 10 %. | Signed QA report; traceability diff; pen-test report; perf report |
| 7 Production readiness | Stage 6 exit. Production config prepared. | On **real** hosts: header contract (S-06/S-07) re-verified. Content domain registered and serving. clamd signature freshness alerting. Disk-usage alerts at 70/85/95 %. Backup and restore drill **including the storage volume** with consistency check (rows ↔ objects). Runbook for malware incident and takedown of content. Rate limits verified through Cloudflare and Nginx (real client IP). Rollback plan tested. | Drill record with timings; screenshots of alerts firing; curl header captures from prod hosts |

---

## 5. Defect workflow and severity

**Workflow.** QA files `DEF-P1-###` with the spec or PRD reference, exact repro (command, input file hash), expected vs actual, evidence (output, file:line) and proposed severity. Then: New → Triaged (owner engineer and severity agreed within 1 working day for S1/S2) → In progress → Fixed (developer links commit and test) → **Verified by QA re-running it**; a developer's claim alone never closes a defect → Closed. A failing re-verification sends it back to Reopened. Every S1/S2 fix must ship with a regression test that fails without the fix (QA checks this by reverting it locally, or via a Stryker mutant). Waivers need the product owner and security owner, an expiry date and a tracking task.

| Severity | Definition | Examples | Release |
|---|---|---|---|
| S1 Critical | Security breach, data loss of acknowledged content, or a spec guarantee (§8 R1-R9) or MUST NOT (§11) violated | Script runs in `zaa.gl`/`app.zaa.gl`/`api` origin; IDOR returns another user's file; malware served; burn file downloaded twice; scan fail-open; XFF spoof bypasses limits (F-02); acknowledged upload lost | **Blocks** any stage exit and release. Hotfix in production. |
| S2 High | A major function broken or a security control weakened without a direct exploit | Redirect limiter missing (F-03); orphaned bytes never deleted; resume broken for > 1 GB; quota bypass by parallelism; CSP missing on non-content pages | **Blocks release.** Stage exit only with waiver. |
| S3 Medium | A function degraded, a workaround exists, or a test-estate defect | Flaky test (F-01); a non-security header deviation; wrong error code | Allowed at release with owner and date (≤ 1 sprint) |
| S4 Low | Cosmetic, docs, minor spec wording | F-09 | Backlog |

**Release is blocked by:** any open S1/S2; any P0 matrix row not PASS without waiver; Phase 0 suite not green; a flaky rate > 1 % over the last 20 CI runs; missing evidence for any gate.

---

## 6. Tasks

| ID | Title | P | Description | Depends | Acceptance criteria | Evidence |
|---|---|---|---|---|---|---|
| QA-001 | Confirm and document the AdminApiTests flake | P0 | Run the forensic SQL of §1.3. Reproduce with `dotnet test --filter "FullyQualifiedName~AdminApiTests\|FullyQualifiedName~AbuseControlsTests"` × 30 and record the failure rate. File DEF for F-01 against the test owner. | PG up | Failure rate measured. Root cause confirmed or refuted with data. | Run log, SQL output, DEF id |
| QA-002 | Per-class test database harness | P0 | Implement §2.3 (env var, template DB from the real migrator, per-class clone, `zaagl` guard, sweeper). Replace the 12 hard-coded strings. | QA-001 | All existing tests pass 10/10 parallel full-suite runs. No writes to the dev `zaagl` DB (verified by row counts before and after). | CI links, row-count diff |
| QA-003 | Isolate global-sweep tests | P0 | Re-check, purge, outbox dispatcher, alloc wipe and freeze deletes each own a database, or run in the `GlobalSweeps` non-parallel collection. | QA-002 | No test mutates rows it did not create (assertion helper). Flake rate 0/50. | Run stats |
| QA-004 | CI pipeline | P0 | GitHub Actions in both repos: build, analyzers, backend tests with a PG 18 service, frontend lint and type-check. Publishes TRX and coverage. | QA-002 | Green pipeline on `main`. Required check on PRs. | Workflow URL |
| QA-005 | Testcontainers in CI | P1 | Postgres, clamd and Nginx fixtures. Locally they skip with `BLOCKED: no docker`. | QA-004 | Container tests run in CI. Locally they report BLOCKED, never pass. | CI log, local log |
| QA-006 | `Zaagl.Api.Tests` WAF harness | P0 | WebApplicationFactory with fake clock, failpoints, fake coordinator lease source, temp storage, fake scanner, Mailpit stub. Host-header helpers for short, api and content hosts. | QA-002 | A sample test creates a link via HTTP and redirects via the short host. | Test run |
| QA-007 | Link creation safety net | P0 | HTTP tests for create (generated and alias), namespace rules, quota, idempotency, guest. Calls the product code, not query copies (fixes F-05 pattern). | QA-006 | Tests cover §7.2 phases A-D happy and refusal paths. Mutating `CreateAsync` fails at least one test. | Run + Stryker spot check |
| QA-008 | Redirect endpoint safety net | P0 | Status per state, 302 only, headers, HEAD, no Set-Cookie, reserved, 1-3 character and multi-segment 404 (F-08), host routing 421, cache invalidation latency. | QA-006 | All §7.3 bullets have a test. F-06/F-08/F-09 confirmed or refuted. | Run, DEF ids |
| QA-009 | AccessGate and unlock safety net | P0 | Password, expiry, click limit (atomic under 50 parallel clicks), fallback, unlock rate limit. | QA-006 | Exactly N clicks succeed at limit N. The 11th unlock in a minute returns 429. | Run |
| QA-010 | Generated authorization matrix | P0 | Route × principal generator (§7.4) over all current routes. Auto-extends to Phase 1 routes. | QA-006 | Every route is covered. A new route without a matrix entry fails the build. | Matrix report |
| QA-011 | Rate-limit and client-IP tests | P0 | Verify F-02 (spoofed XFF from a non-proxy) and F-03 (IPv6 /64 partitioning, missing redirect limiter). | QA-006 | Results recorded. DEFs filed. Tests then guard the fixes. | Run, DEF ids |
| QA-012 | Takedown and NOTIFY invalidation | P1 | Disable a cached link and measure exposure. Stall the listener and check the bypass within 15 s (§8.2). | QA-006 | Measured exposure recorded. Within the R5 bound. | Timing log |
| QA-013 | Redirect latency baseline | P0 | BenchmarkDotNet hot path plus k6 §8.3 profile on a staging-shaped host. PR relative check. | QA-004 | Baseline stored. PR job flags > 10 % regression. | Bench report |
| QA-014 | Architecture tests | P1 | NetArchTest rules from §10 plus Phase 1 rules (storage keys only via the factory, no storage path from request data, Contracts-only module access). | QA-004 | Rules run in CI. A deliberate violation fails. | CI log |
| QA-015 | Phase 1 threat model review | P0 | STRIDE over the upload, scan, publish, view and download flows, burn, desktop and CLI. Map to the §3 matrix. Review the engineering designs. | designs | Every threat has a matrix row or an accepted risk. Signed at Stage 1. | Review notes |
| QA-016 | Content-origin header contract suite | P0 | S-06/S-07 table-driven over every content type × route × host, including error responses. | QA-006, Q-01 | 100 % rows PASS. No Set-Cookie on content and short hosts. | Header matrix |
| QA-017 | Markdown sanitizer corpus | P0 | S-04 corpus as xUnit theories against Markdig + HtmlSanitizer config. Output snapshots. | snippet design | 0 executable constructs survive. Snapshot changes need QA review. | Run, snapshots |
| QA-018 | Snippet E2E and XSS execution oracle | P0 | Playwright: create, view, raw, diff, Copy Raw. Corpus rendered in the real viewer with dialog, network and CSP-report oracles. | QA-039, QA-017 | No execution across Chromium, Firefox and WebKit. | Traces |
| QA-019 | Snippet API contract | P1 | OpenAPI snapshot, size and language limits, expiry, problem codes. | QA-006 | Snapshot approved. Limits enforced at the boundaries. | Snapshot diff |
| QA-020 | CLI snippet and terminal piping | P1 | `cmd | zaagl` streaming, encoding (UTF-8, UTF-16 from PowerShell), ANSI escape stripping or preserving per spec, exit codes. | CLI build | All matrix rows PASS on the 3 RIDs. | Logs |
| QA-021 | Content-type detection and polyglot corpus | P0 | S-01, S-05 corpus with stored hashes. | upload design | Every corpus file classified as expected. No polyglot served inline in a credentialed origin. | Corpus table |
| QA-022 | SVG security suite | P0 | S-03 cases. Decide sanitise vs rasterise vs attachment (Q-02). | QA-016 | 0 script execution. No external fetch observed. | Traces, network log |
| QA-023 | Image and video bomb suite | P0 | S-11 corpus. Worker memory and time limits. | media worker | All bombs rejected with a stable error. No OOM. API p95 unaffected. | Memory graph |
| QA-024 | Media fidelity and viewer E2E | P1 | SHA-256 round trip, colour profile kept, canvas modes, loupe, GIF and recording playback. | QA-039 | Hash equal for every format. Viewer checks PASS. | Hash log, screenshots |
| QA-025 | Malware scanning fail-closed | P0 | S-02 incl. clamd down, timeout, signature age, EICAR variants. | QA-005 | No infected or unscanned byte is ever downloadable. | Run (C: BLOCKED locally) |
| QA-026 | Filename, traversal and header injection | P0 | S-12 with an FsCheck property test on the key factory and the disposition builder. | upload design | 10k generated inputs. 0 escapes. Header has no CRLF. | Run |
| QA-027 | IDOR suite for snippets, files and media | P0 | S-09 plus tus session hijack, thumbnails, previews, metadata leak. | QA-010 | Non-owner always gets 404. No owner data in public payloads. | Matrix report |
| QA-028 | Storage isolation | P0 | S-13 against Nginx internal location and filesystem permissions. | QA-005 | Direct and internal paths are unreachable. | curl captures |
| QA-029 | Download token tests | P0 | S-14 with FakeTimeProvider and key rotation. | token design | All negative cases are rejected. Comparison is constant-time (code review). | Run |
| QA-030 | Burn-after-download race suite | P0 | S-15 at N = 2/10/50, 20 repetitions, both orders via failpoints, preview-bot UAs, Range and HEAD. | QA-006, Q-03 | Exactly one full delivery in every run. Failpoints proven hit. | Stats table, counters |
| QA-031 | Expiry, deletion and orphan reconciliation | P0 | S-16 with FakeTimeProvider and crash at purge failpoints. | QA-002 | Rows and objects consistent after every scenario (checker). | Checker output |
| QA-032 | Quota bypass suite | P0 | S-17 incl. parallel sessions and lying `Upload-Length`. | upload design | No user exceeds quota by more than one in-flight chunk (documented bound). | Run |
| QA-033 | Upload abuse and limits | P0 | S-10: slowloris, abandoned sessions, temp-disk bound, per-/64 limits. | QA-011 | Temp disk stays below its bound under the abuse script. Limits engage. | k6 report, df log |
| QA-034 | Large-transfer performance | P1 | 5 GB upload and resume, 200 concurrent downloads, API memory flat. Content-host path bypasses Cloudflare. | staging host | §2.4 targets met. | k6 + OTel report |
| QA-035 | Recovery suite | P0 | Interrupted upload resume, API kill mid-PATCH, worker kill mid-scan, clamd down, ENOSPC in temp and objects, DB down at finalize. | §2.2 seams | No partial object published. No dangling session after the sweep. Clear user-facing error. | Failpoint counters, logs |
| QA-036 | Desktop security review | P0 | S-19/S-20: credential store, capabilities, CSP, IPC validation, updater signature, signing. | desktop build | Checklist 100 % PASS or waived. | Checklist, screenshots |
| QA-037 | Desktop functional matrix | P1 | Hotkey on Win and macOS. Clipboard types (URL, text, code, image, file, multiple files, huge text). Offline. Proxy. Hotkey taken by another app. Multi-monitor. Sleep and resume. | desktop build | Matrix PASS. | Matrix |
| QA-038 | CLI token handling and AOT smoke | P0 | S-21 incl. a process-list check, config ACL and redaction. | CLI build | No token visible in `ps`/Task Manager command line or logs. | Captures |
| QA-039 | Frontend test bootstrap | P0 | Add Vitest (units) and Playwright (E2E) to Zaagl-Frontend with a CI job and a compose-based test environment. | QA-004 | One E2E (login → create link → redirect) green in CI. | CI link |
| QA-040 | Token storage risk (F-04) | P0 | DEF against frontend: move to `__Host-` cookie BFF (spec §1.2, snapshot "still needed") **before** any user content renders in the app origin. | none | Fixed, or Stage 2 waiver showing the viewer cannot run script in the app origin. | DEF, test |
| QA-041 | Account deletion and export with content | P1 | §7.14 extended: bytes removed, export lists content metadata. | QA-031 | Deleted user's bytes are gone within the SLA. Export is complete. | Run |
| QA-042 | Content takedown and abuse flows | P1 | S-23: report, admin disable, R5 bound, hash blocklist. | QA-012 | Downloads stop within the bound. Re-upload refused. | Timing log |
| QA-043 | Traceability for Phase 1 + checker | P1 | Add Phase 1 rows to `TRACEABILITY.md`. Build `tools/traceability-check` (fails on a missing, skipped or assertion-less cited test). | QA-004 | Checker in CI. Every P0 row cites a test. | CI log |
| QA-044 | Mutation testing on security code | P2 | Stryker.NET on the type detector, sanitizer wrapper, token service, burn claim, key factory and quota. | QA-004 | Score ≥ 80 %. Surviving mutants reviewed. | Stryker HTML |
| QA-045 | Stage 6 exploratory pen-test | P0 | ZAP/Burp on staging plus a manual polyglot and bypass session over all Phase 1 surfaces. | Stages 2-5 | No open S1/S2. | Report |
| QA-046 | Production readiness verification | P0 | Stage 7 checklist on real hosts, incl. a backup and restore drill of DB **and** storage volume. | QA-045 | All Stage 7 exit items PASS. | Drill record |
| QA-047 | Preview-bot behaviour on content links | P0 | Slack, WhatsApp, Teams, Discord and Outlook SafeLinks fetch the landing pages: no consumption, no private bytes in OG tags, password-gated items show no preview. | QA-030 | Recorded per bot UA. Real unfurl on staging. | Screenshots |
| QA-048 | Metadata privacy decision test | P1 | S-24 once Q-06 is decided. | Q-06 | Behaviour matches the decision in every format. | exiftool diffs |

---

## 7. Risks and open questions

**Environment and estate risks**

| ID | Risk | Effect | Mitigation |
|---|---|---|---|
| R-01 | No Docker locally (role rule; confirmed this session) | clamd, Nginx X-Accel, container chaos and Testcontainers are BLOCKED locally | CI with containers is a Stage 1 exit item (QA-004/005). Local reports say BLOCKED explicitly. |
| R-02 | Local PostgreSQL was down during this audit | The suite was not executed. All findings are code-read only. | QA-001 re-runs before Stage 1 exit. |
| R-03 | Tests share the dev database | False failures, polluted dev data, and the `alloc` wipe can corrupt dev allocator state | QA-002/003 before any Phase 1 test is written |
| R-04 | No CI in either repo | No objective "green". Regressions go unnoticed. | QA-004 |
| R-05 | Frontend has no test framework, and tokens sit in `localStorage` | XSS has high impact and cannot be caught automatically | QA-039, QA-040 |
| R-06 | Content host is DNS-only, bypassing Cloudflare (tech stack §09) | The origin IP is exposed. All abuse limits must work at Nginx and the API. F-02 makes this worse. | QA-011, QA-033 on a staging host shaped like production |
| R-07 | Single VPS with local disk for 5 GB files | Disk full affects PostgreSQL too unless volumes are separate | QA-035 ENOSPC tests. Stage 7 separate-volume check. |
| R-08 | Performance targets need a staging-shaped host (§8.3) | No reliable numbers on a laptop or shared runner | Budget a staging host by Stage 4 |
| R-09 | macOS testing needs Apple hardware and a Developer ID; Windows needs an EV certificate | Stage 5 BLOCKED without them | Procure early (PRD risk register) |
| R-10 | Scope conflict: spec v9.4 is "short links only" and §4.5 rule 6 is the only content rule. The PRD drives Phase 1 (snapshot "Decisions needed"). | No spec oracle for Phase 1 behaviour | Phase 1 needs a spec addendum. QA uses it as the oracle. |

**Open questions (owner: product + engineering; QA needs answers before the named stage)**

- Q-01 (Stage 1): Which origin serves user bytes and viewers? A separate registrable domain (tech stack §09 recommends one) vs `zaa.gl/f/<id>` with attachment and sandbox (§4.5 rule 6). Do the viewers live on `app.zaa.gl`, and how are they isolated from the token-bearing origin?
- Q-02 (Stage 3): SVG policy: sanitise, rasterise for preview, or serve the original only as an attachment from the content origin?
- Q-03 (Stage 1): Burn semantics. What consumes the file: claim on POST, first byte, or completed transfer? Are ranged resumes allowed after the claim, and for how long? What happens to a download interrupted at 99 %? Can the owner download without consuming it?
- Q-04 (Stage 4): "Delete after X days": the allowed X per plan, and the relation to the Phase 0 rule that every link expires with quarantine (snapshot E0.3). Is there a quarantine for content bytes?
- Q-05 (Stage 2): Can guests (§7.12) create snippets or upload? If so, what are the limits, and do the content links follow guest rules (no stats, 7-day max)?
- Q-06 (Stage 3): Does "100 % original" keep EXIF/GPS metadata? This is a privacy risk for artists sharing photos.
- Q-07 (Stage 4): After a malware or abuse takedown, are the bytes deleted at once or kept as evidence (and where, under which access)? Do backups keep deleted user content, and for how long?
- Q-08 (Stage 2): The CLI and desktop app authenticate with API keys, which are premium-only (§7.4), or with a new device-token flow? The new scopes needed are `content:write`/`content:read`.
- Q-09 (Stage 5): Does the tray hotkey upload immediately or show a confirmation? Secret detection on the clipboard?
- Q-10 (Stage 1): Is the redirect latency target still the PRD's 50 ms or spec's 5/15 ms p95 server-side, now that content landing pages share the short host?
- Q-11 (Stage 4): Is malware scanning required for snippets (text) and for images that are re-encoded for thumbnails, or only for the file and original download path?
- Q-12 (Stage 1): Who owns fixing F-02..F-10? These are Phase 0 defects that Phase 1 security depends on. QA proposes treating F-02 and F-04 as Stage 1 or Stage 2 blockers.
