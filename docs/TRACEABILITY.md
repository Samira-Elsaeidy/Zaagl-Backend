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

## Deferred entirely (not started this phase)

Every MUST NOT and guarantee tied to: host routing (§1.2), guest tier (§7.12), quarantine/purge
lifecycle (§4.3, §7.8), accounts rework beyond the fingerprint check (§7.4), email (§7.10), abuse
controls (§7.15), the allocator sink as a real S3-compatible bucket (§6.4), chaos/failpoint test suites
(§8.2, §8.3), NetArchTest architecture rules, Stryker mutation testing, and `tools/traceability-check`
itself. None of these have rows here because fabricating "covered" rows for unbuilt work is explicitly
against this document's purpose.
