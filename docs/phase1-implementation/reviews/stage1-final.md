# Stage 1 final review and completion check

Reviewer: zaagl-head. Date: 2026-11-09. Backend `C:\Users\pc\Zaagl-Backend` (branch `phase1/stage-1`, HEAD `9923526`), frontend `C:\Users\pc\Zaagl-Frontend` (HEAD `985948d`). Nothing is committed in either repo, and there are no new branches. The four old `.claude/worktrees/agent-*` worktrees (at `833963a`) predate this stage (U-13).

I read the diffs and new files myself. As instructed, I ran **no build and no test suite**: the lead's full verification run is the evidence of record. Every "passes" claim in the agents' reports is therefore unverified here.

## 1. Verdicts

| Item | Verdict | Owner |
|---|---|---|
| BE-003 plans from config, capabilities, Stripe price→tier, `GET /api/billing/me` capabilities | ACCEPT WITH NOTES (B2, B3) | backend, architect |
| BE-003 API keys stop on downgrade (`IPlanChangeListener`, `ApiKeyPlanChangeListener`, plan check in key auth) | ACCEPT WITH NOTES (B2), plus question Q1 | backend, architect |
| `NativeClientPolicy` on Interactive/Admin | ACCEPT | – |
| BE-005 `Hosts:UserContent`, no Set-Cookie on short/usercontent, inert headers, `RequestBodyLimits` 2 MiB | ACCEPT | – |
| BE-006 Resources schema, `ResourceRegistry`, `ContentQuota`, `ResourceStateCalculator` | ACCEPT WITH NOTES (quota and registry are NOT_TESTED) | backend |
| BE-007 content links: issuer, 409 `content-link`, URL-only list, 302 to `/p|/f|/i`, takedown/export/archive/purge, `link:v2` + NOTIFY fix | ACCEPT WITH NOTES (B4) | backend, architect |
| Migration `AddLinkTargetKind` | **REWORK: must fix before commit** (B1) | backend |
| Migrations `AddLinkResourceIndex`, `AddArchiveTargetKind`, Resources `Initial` | ACCEPT | – |
| Round-3 follow-ups: abandoned cache write distrusted, task attempt fencing + extender retry, C9 error codes | ACCEPT | – |
| FE-003 CSP (`proxy.ts`, `lib/csp.ts`, `shell-hashes.ts`, `(library)`, mui-bridge nonce) | ACCEPT WITH NOTES (F1, F3) | frontend |
| `/unlock` hydration error in Arabic | OPEN defect, fix before Stage 2 (F1) | frontend |
| FE-004 `sanitizeFilename` backslash fix | ACCEPT (regex `/[<>:"/\\|?*]/g`, test covers `..\..\evil.exe`) | – |
| FE-004 `formatBytes` | **REWORK before Stage 2** (F2: decimal units, but PD-54 says binary) | frontend |
| DevOps worker Dockerfile, compose `worker` + `storage` volume, api mount, UID 10001 | ACCEPT WITH NOTES (D1) | devops |
| DEVOPS_PLAN §3 rewritten to the approved layout | ACCEPT | – |
| ops-001 WAF C3 (GET/HEAD/OPTIONS, Nginx answers the preflight) | ACCEPT | – |
| Architect: addendum v1.4, contracts.md, design notes, ADR-0023 final | ACCEPT WITH NOTES (A1–A4: docs lag the code) | architect |

## 2. Backend findings

**B1. `AddLinkTargetKind` validates its constraints under an ACCESS EXCLUSIVE lock. Must fix before commit.**
- The file adds the four CHECK constraints `NOT VALID`, then runs `VALIDATE CONSTRAINT` **in the same migration** (`20261109175124_AddLinkTargetKind.cs`, the final `migrationBuilder.Sql`).
- EF runs one migration's statements in one transaction. The ACCESS EXCLUSIVE lock taken by `ADD COLUMN`/`ADD CONSTRAINT` is therefore still held while `VALIDATE` scans `links.links` three times.
- On a large table, every redirect read blocks for the whole scan. The comment ("validated separately, SHARE UPDATE EXCLUSIVE only") does not describe what the code does.
- The other statements in it are cheap and safe:
  - `DROP NOT NULL`, and `ADD COLUMN` with a constant default, are metadata-only on PG 11+.
  - `link_key_hash` is nullable.
- **Fix (backend):**
  1. Move the four `VALIDATE CONSTRAINT` statements into a separate, later migration, one `Sql(..., suppressTransaction: true)` per statement, so each commits on its own and holds only SHARE UPDATE EXCLUSIVE.
  2. Start `AddLinkTargetKind` with `SET LOCAL lock_timeout = '5s'`, so a long-running query fails the migration instead of queueing every reader behind the ALTER.
  3. Correct the comment.
- No snapshot change is needed (the model already declares the constraints).
- Re-run the Links tests afterwards.
- The other migrations are expand-only and cheap:
  - `AddLinkResourceIndex` is `CREATE UNIQUE INDEX CONCURRENTLY`, non-transactional, with recovery steps documented.
  - `AddArchiveTargetKind` has metadata-only statements.
  - Resources `Initial` is a new schema only.

**B2. Revoking keys on downgrade is "after commit", and is lost if the listener fails.**
- The parts that work:
  - `BillingService.AfterPlanChangeAsync` calls the listeners after Billing's `SaveChanges`.
  - Key authentication rejects revoked keys directly in the database, and also re-checks `Capabilities.ApiKeys` through a 60 s per-instance cache.
  - So a downgraded key stops working within at most 60 s even if revocation never happens.
- The gap:
  - If the listener throws (it is logged and swallowed) or the process dies between commit and listener, the keys are never revoked.
  - A later Stripe event with the same tier returns early (`previousTier == newTier`), so nothing retries.
  - After a later upgrade those keys work again. Spec §8.2 (L901, L1186) says "an upgrade does not restore them".
- **Fix (backend, small, before Stage 2):** make revocation self-healing.
  - Call the listeners on every processed subscription event whose resulting plan lacks `ApiKeys`, not only on a tier change.
  - Have `ApiKeyPlanChangeListener` revoke whenever `!current.ApiKeys` (the revoke is already idempotent).
- A transactional guarantee needs a cross-module outbox; whether to wait for it is question Q1.
- Also: `RevokeApiKeysForPlanChangeAsync` uses `TimeProvider.System` (the comment says so). Inject the clock when Identity gets one.

**B3. `GET /api/billing/me` is now `Interactive` only.** This matches the round-2 R1 fix (accepted), so API keys and device tokens get 403. Already approved; recorded only because it is a Phase 0 behaviour change.

**B4. The plain link key on an idempotent replay.**
- The issuer returns `LinkKey = null` on a replay.
- contracts.md C1 says "the default is a key rotation on replay", while ARC-002 Q1 is open.
- No endpoint uses the issuer yet. Align the two before the Stage 2 snippet endpoint (architect states one rule; backend follows it).

**Other checks (OK):**
- No `PlanTier` comparison outside `src/Modules/Billing`. I grepped `src/`; the only tier switches are in `BillingService`/`ContentPlans`, plus the Phase 0 `PlanLimits` in Billing contracts.
- Plan values match PD-04/06/07/54/56:

  | | Guest | Free | Starter |
  |---|---|---|---|
  | File limit (MiB) | 25 | 25 | 100 |
  | Snippet limit | 256 KiB | 512 KiB | 2 MiB |
  | Retention (days) | 7 | 7 | 30 |
  | Quarantine | no | yes | yes |
  | Storage quota | none | 250 MiB | 5 GiB |
  | Burn after download and password | no | no | yes |

- `ValidateOnStart` fails startup on a missing or invalid section.
- The 2 MiB Kestrel default is raised only on the CSV import route. Delivery is reachable only on the user-content host, never on the api/short hosts.
- Content links skip URL steps. They are excluded from the safety re-check (`target_kind = 0`). Takedown records `ResourceId`. Export and archive carry the kind.
- `LinkCacheInvalidationListener` now uses `LinkCache.Key` (the `link:v2` bug is fixed).

## 3. Frontend findings

- **F1. `/unlock` hydration error in Arabic. Cause not confirmed; fix before Stage 2.**
  - Likely cause: `LocaleProvider` switches to `ar` from `localStorage` in a `useEffect` after the first render. FE-003 made `/unlock` per-request with a streamed `<Suspense>`. A context change that reaches a Suspense boundary that has not finished hydrating forces React to client-render it, which it logs as a hydration error. It only happens when the stored locale is not `en`.
  - This affects every class N page that streams under Suspense: the viewers, sign-in, and the library.
  - The frontend agent must reproduce it, capture the exact console error, and fix it. Options: defer the locale switch until hydration completes, or resolve the locale on the server.
  - The round-3 "`next dev` `[slug]` 500" is still unexplained (same owner).
- **F2. `formatBytes` divides by 1000.** PD-54 fixes 1 MB = 1,048,576 bytes, so the Free 25 MB limit (26,214,400 bytes) would display as "26 MB". Change it to 1024 and fix the tests (`format.test.ts:19-21`). Not used on any screen yet.
- **F3. Small ADR-0023 drifts.**
  - `worker-src` is `'self'` in code, but `'self' blob:` in the ADR (react-pdf will need `blob:` in Stage 3).
  - Class S `media-src` lacks `blob:`.
  - `.env.example` says the origin matches the backend's `Content:UserContentBaseUrl`, but the backend key is `Hosts:UserContent` (a host name).
  - Align them in the ADR or the code.
- FE-003 is otherwise as approved (PD-53):
  - Class N uses nonce + `'strict-dynamic'`.
  - Class S uses build hashes + nonce, without `'strict-dynamic'`.
  - No `'unsafe-inline'` for scripts.
  - `connect-src <uc>` on viewers.
  - The library is in class N, as recorded in the ADR.
  - The Emotion nonce comes from Next's internal `HeadManagerContext`. This is an accepted, documented upgrade risk.
  - `/bff` gets its own `default-src 'none'` policy.

## 4. DevOps findings

- **D1.** The worker image and compose service match ARC-012:
  - backend network only;
  - no port;
  - liveness file healthcheck;
  - `stop_grace_period` 65 s;
  - shared `storage` volume with the api at `/srv/zaagl/storage`;
  - UID 10001 in all three images.
- Notes:
  - Existing named volumes created by the old images (random UID) need a one-time `chown 10001:10001`. The Dockerfiles say so; add it to the RUNBOOK upgrade steps.
  - `mem_limit: 6g` with no CPU limit is fine for development. Production limits belong to the production compose (OPS-007).
  - No Docker run was done: NOT_TESTED.
- C3 now allows OPTIONS, and Nginx answers the preflight with 204, so it never reaches the API. This is consistent with ARC-008 §9/§10.
- DEVOPS_PLAN §3 is rewritten to the approved layout. `www` is correctly marked PROPOSED (U-11).

## 5. Architect findings (docs lag the code)

- **A1.** `IPlanChangeListener` is a post-freeze C5 change.
  - It is not in contracts.md, ARC-005 or the addendum.
  - Record it under the change rule (contracts.md:24), with the "after commit, not in the same transaction" deviation from spec §8.2 (pending Q1).
- **A2.** ARC-005 (line ~80) says "Keys are not deleted, so an upgrade revives them". That contradicts spec L901/L1186 and the built code (revoke, never restore). Correct it.
- **A3.** ARC-004:17 and contracts.md C1 say the issuer mints link keys through Access's `ILinkKeys`.
  - Links cannot reference Access (Access references Links), so the issuer mints the key itself: 16 CSPRNG bytes, base64url, SHA-256 of the **raw bytes**.
  - Record this. Do one of:
    - move `ILinkKeys` into Links contracts or SharedKernel;
    - or state that Access's future `LinkKeys.Matches` must base64url-decode and hash the raw 16 bytes, not the string.
  - Otherwise every keyed link will fail to unlock in Stage 2.
- **A4.** Replay rule (B4) and `ResourceState` "Error → Processing" (code) should be confirmed in ARC-006.
- List A from round 3 is applied where I spot-checked:
  - `416 range-required`;
  - `resources:write` marked OPEN;
  - `busy`/`insufficient-storage`/`content-link` in the catalogue;
  - approvals up to PD-57.
- Addendum v1.4 applies PD-15/25/28/53–57.

## 6. Must fix before commit

1. B1 migration (backend), then re-run the Links tests.
2. Repository hygiene (lead):
   - Do not commit `.claude/worktrees/`, `.claude/settings.json.bak`, or `tests/**/TestResults`. `TestResults` is already ignored; the other two are not.
   - Replace the roughly 180 per-file `graphify-out/cache/...` lines added to `.gitignore` with the single `graphify-out/` line, which is already present. The added `.graphifyignore` line is also suspect: ignoring that file means its rules are never shared.
3. STATUS.md still lists every Stage 1 task as NOT_STARTED. The lead must update it, and SESSION_HANDOFF.md, before the commit.

**Before Stage 2 (not blocking the commit):** B2, B4, A1–A4, F1, F2, F3; the dev-server 500; and the B-notes from round 3 (ARC-012 dedupe rule, RUNBOOK Valkey line).

## 7. Stage 1 completion check (from files, not reports)

| Task | Result | Evidence / what is missing |
|---|---|---|
| ARC-002 | DONE | `design/ARC-002-link-target-model.md`; built by BE-007 |
| ARC-004 | DONE | `design/ARC-004-access-gate.md`, `Access/Contracts/AccessContracts.cs`; A3 doc fix |
| ARC-005 | DONE | `design/ARC-005-plan-model-v2.md`; A2 doc fix |
| ARC-006 | DONE | `design/ARC-006-resource-lifecycle-quota.md` |
| ARC-008 | DONE | `design/ARC-008-usercontent-delivery.md` |
| ARC-012 | DONE | `design/ARC-012-job-hosting.md`; PD-25 approved |
| ARC-014 | DONE (design) | `design/ARC-014-native-client-auth.md`; PD-33 still open (Q3) |
| ARC-016 | DONE | `design/openapi-phase1.yaml`, list A applied |
| Contract freeze | DONE | contracts.md:6; one unrecorded post-freeze change (A1) |
| BE-001 | DONE | `ApiScopes.cs`, `RequireScope` on every links/folders route, `ApiScopeTests.cs` |
| BE-002 | DONE | `Result.cs` `ErrorCodes`, `ApiResults.Normalize`, `ProblemTypeTests.cs` |
| BE-003 | DONE | `ContentPlans.cs`, `BillingService.cs`, `ApiKeyPlanChangeListener.cs`; B2 |
| BE-004 | DONE | `Files/Infrastructure/LocalObjectStorage.cs`, `StorageKeys.cs`, tests |
| BE-005 | DONE (Stage 1 scope) | `HostRouting.cs`, `RequestBodyLimits.cs`, `UserContentHostTests.cs`; delivery endpoints are BE-017/018 |
| BE-006 | DONE, NOT_TESTED | Resources module + migration; no test touches `ContentQuota`/`ResourceRegistry` (testing deferred, PD-51) |
| BE-007 | PARTIAL until B1 is fixed | Issuer, routing, migrations, `Phase1GroundworkTests.cs` |
| BE-013 | DONE | `SharedKernel/Jobs`, `files.tasks` migration, Worker host, `TaskQueueTests.cs` |
| BE-030 | DONE | `ForwardedHeadersSetup.cs`, `ForwardedHeadersTests.cs` |
| BE-031 | DONE | `RedirectRateLimiter.cs`, route regex 4–64, `RedirectHardeningTests.cs` |
| FE-001 | PARTIAL | Next 16 docs findings, `config.ts`/`.env.example` origins done. Missing: `(viewer)` group, `ViewerLayout`, viewer `loading/error/not-found`, nav entries for `/paste/new` and `/files`, Playwright nav/no-cookie test |
| FE-002 | DONE | Vitest/RTL/MSW/Playwright config; E2E NOT_TESTED (no `E2E_BASE_URL`) |
| FE-003 | DONE with open defect | `proxy.ts`, `lib/csp.ts`, `shell-hashes.ts`; F1 |
| FE-004 | PARTIAL | `filename.ts` fixed; `formatBytes` must follow PD-54 (F2) |
| FE-005 | DONE | `AccessPolicyFields`, `OrganizeFields`, `ShareResultCard` |
| FE-006 | DONE (drafts) | `content-types.ts`, `features/snippets/api.ts`, `features/files/api.ts` |
| FE-027 | DONE | `auth-transport.ts`, `session-store.ts`, token-store removed |
| FE-029 | DONE | `src/server/bff/**`, `src/app/bff/**` |
| OPS-001 | DONE (plan); execution BLOCKED | `design/ops-001-hosts-and-dns.md`; accounts and PD-52 pending, nothing applied |
| OPS-003 | PARTIAL | UID 10001, non-root, `.dockerignore`, worker image done; base-image digest pinning not done; image build NOT_TESTED |
| OPS-004 | DONE, NOT_TESTED | `.github/workflows/backend-ci.yml` (no GitHub run yet) |
| OPS-005 | DONE, NOT_TESTED | `migrations-check` job discovers every DbContext (covers the new Resources/Files contexts) |
| OPS-006 | DONE, NOT_TESTED | `Zaagl-Frontend/.github/workflows/frontend-ci.yml` |
| OPS-014 | DONE | `deploy/secrets/README.md` |
| QA-006 | DONE | `tests/Zaagl.Api.Tests/Harness` |
| QA-008 | DONE | `RedirectEndpointTests.cs` |
| QA-009 | DONE | `AccessGateTests.cs` |
| QA-010 | PARTIAL | `AuthorizationMatrixTests.cs` covers part of the matrix (stage1-qa.md) |
| QA-011 | DONE | `RateLimitAndClientIpTests.cs` |

Summary (38 items):
- 33 DONE. Some have NOT_TESTED parts, and OPS-001 is done as a plan with its execution blocked.
- 5 PARTIAL: BE-007 (until B1 is fixed), FE-001, FE-004, OPS-003, QA-010.
- 0 NOT DONE.

## 8. Questions for the user

- **Q1. Turning off API keys on downgrade.**
  - The spec says keys are switched off in the same step as the plan change. The build switches them off a moment later, in a separate step.
  - In the meantime, and if that step ever fails, a key stops working anyway within 60 seconds, because every use re-checks the plan.
  - The remaining gap: if that step fails, the keys are not formally cancelled, so they would work again after a later re-upgrade.
  - Options:
    - (a) Accept "a moment later", with the small fix in B2 that makes it retry itself on the next billing event.
    - (b) Require an exact same-step guarantee. This needs a new reliable-messaging piece between modules, which is Stage 2 work.
  - Recommendation: (a).
- **Q2.** Still open from round 3, unchanged:
  - U-7 CLI sign-in on Linux servers (PD-33);
  - U-8 rest: storage at takedown (PD-55 rest), what counts as a view (PD-10);
  - U-9 Arabic digits;
  - U-10 staging hostnames;
  - U-11 `www` redirect;
  - U-12 procurement;
  - U-13 remove the four old worktrees.

  None of them blocks the Stage 1 commit.
