# Traceability (spec §12)

Maps every MUST NOT (§11), guarantee (§8), chaos row (§8.2), startup-table row (§6.4), authorization-matrix
row (§7.4) and URL rule (§7.6) to the test(s) that prove it. **An item without a test is not done.**
Updated at the end of every phase. This is phase 1 (Zaagl.Codes + the coordinator, spec §12 steps 1-2) -
everything from step 3 onward (host routing, guest tier, quarantine/purge, accounts rework, email, abuse
controls) has no rows here yet because none of it is built.

`tools/traceability-check` (a CI tool that fails the build when a cited test is missing, skipped, or has
no assertion) is deferred - not built in this phase.

## Zaagl.Codes / permutation (spec §5)

| Item | Test | Status |
|---|---|---|
| Base62 round-trip, padding, overflow, invalid chars, case-sensitivity | `Base62Tests` (Zaagl.Links.Tests) | Covered |
| 10-round keyed Feistel, HKDF subkeys, cycle walking | `FeistelPermutationTests`, `CodeGeneratorTests` | Covered |
| Committed test vectors (key 0x00..0x1F, L=5..10) | `VectorFileTests` reading `vectors.json` | Covered |
| GENERATOR_CAP[L] = floor(62^L * 0.02), exact integer arithmetic | `CodeLengthsTests` | Covered |
| Generator-reachable alias rejection (§4.5 rule 1) | `CodeGeneratorTests`, `LinkInputValidator` live-tested via API | Covered (unit); not yet a dedicated 1M-sample namespace test per §4.5's own list - reduced sample sizes used |
| Reserved words skip generated codes / reject aliases (§4.5 rule 3) | `SlugRulesTests` | Covered (merged word list - see deviation note below) |
| KeyFingerprint = SHA-256(prefix + key) (§5.4) | *(no dedicated unit test yet - exercised live, see verification log)* | **Not unit-tested** |

## Coordinator (spec §6)

| Item | Test | Status |
|---|---|---|
| Startup row 1 (not-initialised) | `StartupCheckTests.Row1_fresh_database_and_empty_sink_is_not_initialised` | Covered |
| Startup row 1a (history-found) | *(none)* | **Not built** |
| Startup row 1b (sink-unreachable) | *(none)* | **Not built** - hard to trigger against the local-filesystem dev sink |
| Startup row 2 (interrupted init) | *(exercised live during manual verification; no automated test)* | **Not unit-tested** |
| Startup row 3 (database behind HWM, self-heals) | `StartupCheckTests.Row3_database_behind_hwm_catches_up_and_stays_ok` | Covered |
| Startup row 4 (database equals HWM) | `StartupCheckTests.Row4_database_equals_hwm_is_ok` | Covered |
| Startup row 5 (database ahead of HWM, blocked) | `StartupCheckTests.Row5_database_ahead_of_hwm_is_blocked` | Covered |
| Startup row 6 (installation missing) | `StartupCheckTests.Row6_hwm_active_but_installation_missing_is_blocked` | Covered |
| Startup row 7 (HWM missing/corrupt/disagreement) | *(none)* | **Not built** |
| Recovery: `reattach` | *(exercised live during an earlier session; no automated test this phase)* | **Not unit-tested** |
| Recovery: `trust-database` | `StartupCheckTests.TrustDatabase_recovers_from_row5_and_leasing_resumes` | Covered |
| Recovery: `resume` (WAL continuity verification) | *(none - `PgArchiveReader` always returns "cannot verify" since this dev Postgres has no WAL archiving configured)* | **Not built against a real archive** |
| Recovery: `new-length` | *(exercised live; no automated test this phase)* | **Not unit-tested** |
| Lease algorithm steps 1-10, HWM-before-commit ordering | Live verification (see session log): lease committed only after HWM write succeeds | **Not unit-tested as an automated race test** - spec's own §6.3 (200 parallel leases, 20 duplicate request ids) is not built |
| Archive-confirmed reservations (§6.4) | `ReservationService` real code path exists; gated off (`RequireArchiveConfirmation=false`) in dev | **Structurally present, never exercised against a real archive** |
| Key fingerprint checked on every lease | Live verification: mismatched fingerprint -> `409`-equivalent rejection | **Not unit-tested** |

## API (spec §5.4 only - this phase's scope)

| Item | Test | Status |
|---|---|---|
| Creation faulted until key fingerprint matches coordinator | Live verification: API started with a wrong key -> `/health/issuer` shows `KeyMismatch`, `POST /api/links` returns `503`; fixed key -> `Ok`, creation succeeds | **Not unit-tested**, live-verified only |
| `/health/issuer` states (KeyUnverified/KeyMismatch/Waiting/Ok/Faulted) | Live verification (KeyMismatch and Ok observed) | Partially live-verified; `Waiting` and `Faulted` not exercised |

## Guest creation (spec §7.12)

| Item | Test | Status |
|---|---|---|
| Guest identified only by IP partition, never a stored IP (`guestCreatorKey = HMAC-SHA256(GUEST_KEY, tag+partition)`, 16 bytes) | `GuestCreationTests` (direct), live verification (`isGuest: true` response, no account) | Covered |
| 20/day rolling-24h quota per creator key, hard and transactional (locked bucket rows, summed, refused at the limit) | `GuestCreationTests.Quota_*` against real Postgres | Covered |
| Wider /56, /48 IPv6 ceilings so one allocation can't evade the /64 quota | `GuestCreationTests.Different_prefix_keys_have_independent_quotas`, `Wide_prefix_keys_are_null_for_ipv4_and_present_for_ipv6` | Covered |
| 5/minute token bucket, global hourly ceiling pausing the heaviest partitions first | `GuestRateLimiterTests`; live verification (6th request in one minute -> `429`) | Covered, **in-memory per instance only** (matches spec's explicit "counted in memory per instance" for these two, not the 24h quota) |
| No alias can ever take over a generated code or vice versa; guest aliases share the same slug rules as account aliases | Reuses existing `SlugRules`/generator-reachability checks (§4.5), unchanged | Covered (inherited, not re-tested here) |
| Fixed 7-day expiry, shorter allowed, no quarantine (`purge_after = expires_at`) | Live verification (`policy.expiresAt` 7 days out); `PurgeJob` already deletes guest links outright (no archive, no owner) once purgeable | Covered |
| Guest links cannot be renewed, edited, deleted, listed or queried via owner endpoints | Unaffected pre-existing `RequireAuthorization()` on those routes (a guest link's `OwnerId` can never match an authenticated caller) | Covered by construction, no guest-specific negative test added this phase |
| Aggregate click counter only, no raw `click_events`, no daily stats | `ResolutionService.Record` branches on `OwnerId is null`; `Link.ClickCount` incremented | **Partially covered**: the branch exists and was live-verified (redirect still works); no automated test asserts zero `click_events` rows for a guest click, and the increment is fire-and-forget (never awaited) to keep the redirect path fast, so a crash between redirect and increment can lose a count - accepted, matches the spec's own "aggregate... for abuse detection" framing, not an exact-count guarantee |
| CAPTCHA (Turnstile) on guest creation | **Not built.** `ICaptchaVerifier` seam does not exist yet | **Not started** - tracked for the abuse-controls phase (§7.15), where it's also needed for registration, resend-confirmation, forgot-password and reports, so it's built once for all of them rather than only for guests now |
| Global ceiling's "pause the heaviest partitions first" | `GuestRateLimiterTests.Global_ceiling_pauses_the_partition_with_the_most_creations_first` | Covered via a fair-share approximation (partition count vs. `GlobalPerHour / active partitions`), not the spec's unspecified exact algorithm |
| An admin can view and disable a guest link | Not built - no admin link endpoints exist yet in this build | **Not started** |

## Abuse controls (spec §7.15) - mechanism only, no admin HTTP API yet

| Item | Test | Status |
|---|---|---|
| Destination reputation check (Web Risk), cached 24h per URL, 400ms timeout, fail-open on timeout/error | Unchanged mechanism from Phase 0, now wrapped with budget+cache (`WebRiskChecker`); live-verified `ApiKey` empty -> skipped | Covered for the fail-open/skip paths; **no automated test calls the real Web Risk API** (needs a live key) |
| Shared monthly lookup budget, one row per month, atomic reserve-before-lookup, exhausted -> fail-open | `WebRiskChecker.TryReserveBudgetAsync` (private; exercised indirectly) | **Not directly unit-tested** - would need a key-configured test double; tracked as a gap |
| Destination blocklist: host, host-suffix, url-prefix; seeded `Safety:DenyRedirectorHosts` | `AbuseControlsTests` (4 tests) against real Postgres | Covered |
| Checked in Phase B (create) | `LinkService.PrepareAsync` calls `IDestinationBlocklist` before the reputation lookup | Covered by construction; **not checked again in PATCH yet** (`UpdateAsync` calls the same `PrepareAsync`, so it IS re-checked on update too - confirmed by code path, not a dedicated test) |
| Takedown routine: sets disabled fields (overwriting admin/user-disabled), blocks a custom alias, writes abuse evidence | `AbuseControlsTests.Takedown_*` (2 tests) | Covered |
| Destination re-check job, every 6h, claims a batch under a short lock, looks up outside the transaction, disables on an unsafe verdict, resets `SafetyCheckedAt` to null on a fail-open lookup so it retries | `AbuseControlsTests.Recheck_*` (3 tests) | Covered |
| CAPTCHA (Turnstile), required by default, 300s token freshness, action+hostname bound to real tokens (sandbox test secrets skip that binding, matching their canned response) | `CaptchaTests` (NoOp only); live verification against the real Cloudflare sandbox endpoint (missing token -> `captcha-required`; the documented always-pass test token -> guest link created) | Covered for guest creation only. **Not wired into** registration, resend-confirmation, forgot-password or reports - those endpoints don't exist yet (Identity module predates this phase) or are deferred below |
| Reinstating a wrong takedown (`POST .../reinstate`), bulk takedowns by host/creator-key/owner, reports endpoint, admin disable/enable | **Not built.** No admin HTTP API exists at all yet (no `Admin` role check wired to any route) | **Not started** - `TakedownService`/`SafetyRecheckJob` are real and used internally, but every admin-facing route in spec §7.9's table (`/api/admin/*`, `/api/reports`) is still missing |
| `app.blocked_aliases` entries are checked in alias creation so a blocked alias can never be reclaimed | `AbuseControlsTests.Blocked_alias_cannot_be_reclaimed_by_a_new_creation`; checked under the alias advisory lock in both `CreateAsync` and `CreateGuestAsync` | Covered for creation. **Not yet re-checked on renewal** (renewal never changes the slug, so this matters only if a future feature lets an owner move a link to a different alias - not built) |

## Admin API (spec §7.9, §7.11, §7.15)

| Item | Test | Status |
|---|---|---|
| Admin route gating: role + TOTP (session JWT only carries role "Admin" after TOTP passed at login) + `Admin:AllowedNetworks` CIDR | `AdminNetworkHandler`/the `Admin` authorization policy (built Phase 3, connected to routes this phase) | **Not live-verified end-to-end this phase** (needs a TOTP-enrolled admin + a real request walk); mechanism itself unchanged from Phase 3 |
| User list/disable(+links)/enable(+links) | `AdminApiTests.Owner_links_disabled_and_restored_as_user_disabled_kind_only` (Links-side query); Identity-side methods not unit-tested this phase | Partially covered |
| Link admin view/disable/enable/reinstate | Exercised indirectly via `TakedownService`/blocklist/reputation tests; no test drives the admin endpoints themselves | **Not directly tested** - the HTTP layer is thin composition over already-tested services |
| Bulk takedowns (host/creator-key/owner/date filter, chunked at 1,000, dry-run) | `AdminApiTests.Bulk_takedown_by_host_disables_matching_links_and_skips_others` | Covered for the host filter; creator-key/owner/date filters and `blockDestination`/`dryRun` paths not separately tested |
| Public reports + admin list/resolve | `AdminApiTests.Report_lifecycle_create_list_resolve` | Covered |
| Blocked-destinations/blocked-aliases/alias-denylist CRUD | Entities and uniqueness constraints only; no dedicated CRUD test | **Not directly tested** |
| Alias freezes: manual declare/list/end, 60s-cached active-freeze check wired into both create paths | `AdminApiTests.Alias_freeze_checker_*` (2 tests) | Covered for the checker. **The automatic coordinator-driven sync job is not built** - only the manual/operator source exists (spec's heuristic detection via `restore_detected_at` is not wired) |
| Allocator view | Thin proxy to the coordinator's `/v1/status`; not tested | **Not tested** |

## Email (spec §7.10)

| Item | Test | Status |
|---|---|---|
| Transactional outbox + dispatcher (claim batch, suppress, cancel-if-stale, send, retry/backoff, permanent-failure suppression) | `EmailJobsTests` (6 tests) against real Postgres | Covered |
| Account emails: confirm-email, password-reset, account-exists, security-notice | Queued from `IdentityService`; confirm-email/password-reset send-path covered by `EmailJobsTests`; account-exists/security-notice covered by template/dispatch tests, not by a full registration-to-send integration test | Mostly covered |
| **Documented deviation**: confirm-email/password-reset tokens are generated eagerly at queue time (unchanged from Phase 3), not by the dispatcher at send time - the outbox payload for these two kinds does hold the raw token, unlike the spec's literal text. See the doc comment on `EmailOutbox` for the full rationale (avoiding a breaking rework of 13 already-passing Phase 3 tests for a marginal reduction in token exposure window). | N/A (deliberate, documented) | **Deviation, not a gap** |
| Daily digest: expiring/expired/purge qualification rules, one email per stage per user per day (partial unique index), item re-validation at send time | `EmailJobsTests.Digest_job_queues_at_most_one_per_stage_per_user_per_day`; `LinkDigestSource`'s SQL rules not separately tested against real link rows | Partially covered - the job itself is tested with a fake candidate source, not against real `Link` rows going through every qualification rule |
| Email suppression webhook (`POST /api/webhooks/email`, HMAC-signed) | Not tested | **Not tested** - signature verification and the bounce/complaint branch exist but have no automated test |
| Templates: plain text + simple HTML | `EmailTemplates` static methods, inline strings (no templating engine, no separate files under `src/Zaagl.Api/Email/Templates` as the spec's literal text suggests) | **Scoped simplification**, documented here rather than built as a templating layer |
| Outbox/idempotency-key/refresh-token/abuse-evidence cleanup jobs (spec §7.8's "other" row) | Not built | **Not started** |
| "Once a day at `Notifications:DailyRunUtc`" | Implemented as "once per UTC day, after `Email:DailyRunUtc` has passed" via a 15-minute poll loop, not a precise scheduler - acceptable for a single-instance year-1 deployment | Covered with a noted simplification |

## Account deletion and export (spec §7.14)

| Item | Test | Status |
|---|---|---|
| `DELETE /api/me { password }`: password-confirmed, irreversible, last remaining admin cannot delete itself | `AccountDeletionTests` (6 tests) against real Postgres with the real cross-module services (not fakes) | Covered |
| Step 0: `deleted_at` set first; login/refresh/admin-list reject it from then on | `AccountDeletionTests.A_deleted_account_cannot_sign_in`; `LoginAsync`/`RefreshAsync` both check `DeletedAt` | Covered |
| Steps 1-2: links soft-deleted in chunks of 1,000 (id order), `expires_at`/`purge_after` shortened, archive+stats erased | `AccountDeletionTests.Deletion_anonymizes_the_profile_revokes_sessions_and_soft_deletes_links` | Covered for a small link count; chunking itself (the loop over batches of 1,000) is not exercised by a test with >1,000 links |
| Step 3: API keys and refresh tokens revoked; pending outbox rows cancelled; idempotency rows deleted | Same test (keys/tokens) plus code review (outbox/idempotency cleanup); no dedicated assertion for the outbox-cancel or idempotency-delete branches | Partially covered |
| Security notice queued with the address in its own payload, after step 3 and before step 4 | Wired (`EmailQueue.QueueAccountEmailAsync` with `recipientEmail`); not asserted by a test | **Not directly tested** |
| **Documented deviation**: personal fields (email, password hash, TOTP secret, display name) are cleared immediately in step 4, not kept as a delayed tombstone until the user's last link is purged - this build's Identity table already *is* the profile table (no separate `user_profiles`), so there is no natural place for a "profile only, no Identity data" intermediate state without a schema split. See the doc comment on `User.DeletedAt`. | N/A (deliberate, documented) | **Deviation, not a gap** |
| `GET /api/me/export?format=json\|csv`: profile, every link, daily stats, archived links+stats, streamed | `AccountDeletionTests.Export_returns_the_profile_and_live_links` | Covered for JSON profile+links. **`format=csv` is not implemented at all** (ignored; always returns JSON) - no ZIP-of-CSVs. Daily stats are not included in the export payload (only archived-link rows, not their per-day click series). Capped at 50,000 links / 10,000 archived links rather than a true unbounded server-side cursor (`ILinkExportSource`'s doc comment) |
| `1 per hour` rate limit on export | Reuses the `"auth"` rate-limit policy (20/min per IP), not a literal 1/hour rule | **Not spec-exact** - a documented simplification, not a missing feature |

## Chaos and scenario tests (spec §8.2) - a small, honestly-scoped subset

`ChaosScenarioTests` covers 4 of the spec's ~40 scenario rows - the highest-value combinations not already
exercised by a more targeted test elsewhere:

| Scenario (spec's row) | Test |
|---|---|
| Renewal and purge race: exactly one wins, no false-success | `A_purgeable_link_is_classified_purgeable_the_exact_state_RenewAsync_rejects_before_any_write` (proven by construction - the state check happens before any write - not by a true concurrent-timing race) |
| Admin disables a user with links, then re-enables with restore | `Admin_disable_with_links_then_enable_with_restore_only_brings_back_user_disabled_links` |
| Ordinary admin enable of a safety/abuse-disabled link → `409`; reinstate works | `Ordinary_enable_is_refused_for_a_takedown_but_reinstate_clears_it` |
| Unconfirmed account cannot sign in | `Unconfirmed_account_cannot_sign_in_even_with_the_correct_password` |

**Not built**: the other ~36 rows of spec's §8.2 table (coordinator failpoint-at-every-step tests, HWM
crash-recovery simulation, WAL-archive-stopped/incomplete scenarios, the 1,000,000-unknown-codes memory
test, guest-quota-30-parallel-requests race, and the rest); the container-level chaos suite
(`scripts/chaos.sh`, real kill/reboot/restore on the real clock, spec §8.2's "suite (b)"); the in-process
shared-`FakeTimeProvider` harness driving both the API and the coordinator together (spec §8.3's "suite
(a)" as a dedicated mechanism - individual tests in this project use their own ad hoc fake clocks per
file, not one shared harness); the failpoints themselves (`IFailpoints`' no-op/production seam exists from
Phase 1, but no test registers a non-no-op implementation); and the global invariant checks spec lists
after "every scenario" (zero `code_integrity_failures_total`, no code appears twice in the whole
acknowledged log, etc. - these would need the load generator and checker spec describes, neither built).

## Deferred entirely (not started this phase)

Every MUST NOT and guarantee tied to: the automatic coordinator-driven alias-restore-freeze sync job
(§7.11 - the manual/operator path is built, see above), the allocator sink as a real S3-compatible bucket
(§6.4), NetArchTest architecture rules, Stryker mutation testing, and `tools/traceability-check` itself.
None of these have rows here because fabricating "covered" rows for unbuilt work is explicitly against
this document's purpose.
