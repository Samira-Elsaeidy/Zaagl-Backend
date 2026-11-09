# Stage 1 review, round 3: backend (cache resilience, hub query token removal, BE-002, BE-004, BE-013)

Reviewer: zaagl-head. Date: 2026-11-09. Checkout `C:\Users\pc\Zaagl-Backend`, HEAD `9923526`, nothing committed. I read the code and diffs. I ran no build or tests, because a backend build is in progress. The agent reports Links 486/486; the lead's full run is the evidence of record.

## Verdicts

| Item | Verdict | Owner of follow-up |
|---|---|---|
| Cache resilience: `ResilientDistributedCache.cs`, `LinkCacheEviction.cs`, `Cache:L2` settings | ACCEPT WITH NOTES (notes 1 and 2) | backend |
| Removal of `?access_token=` on `/hubs` (PD-41) | ACCEPT | – |
| BE-002 problem types: `Result.cs` `ErrorCodes`, `ApiResults.StatusFor/Normalize`, `AddProblemDetails` | ACCEPT WITH NOTES (note 3) | backend, architect |
| BE-004 `LocalObjectStorage` + `StorageKeys`, old `IFileStorage` deleted | ACCEPT | – |
| BE-013 `SharedKernel/Jobs` `TaskQueue`/`TaskRunner`, `files.tasks` migration, Worker host | ACCEPT WITH NOTES (notes 4 to 6) | backend, devops |

## Cache resilience and spec §7.3 R5

R5: a disabled, deleted or taken-down link stops redirecting on every healthy instance as soon as the change commits. What I checked:

- **Saving a link no longer fails on a Valkey timeout.** `LinkCacheEviction` runs after commit and catches and counts every failure, on `CancellationToken.None`.
- **Local copy.** HybridCache drops L1 on `RemoveAsync` whatever L2 does. Other instances still get the pg `NOTIFY`, whose path is unchanged.
- **L2 bounded.** Every L2 operation is limited to 250 ms. After 1 failure the circuit opens for 30 s, then exactly one probe goes through (`_probeInFlight`). Reads during an outage become misses, so they go to the database. The tests cover a hanging L2, a throwing L2, the probe and the L1 invalidation.
- **Lost deletes.** A key whose L2 delete failed is never read from L2 for `MaxEntryLifetimeSeconds` (300 s, at least the 5-min snapshot expiry) unless a later delete or write succeeds. Overflow beyond 100,000 keys distrusts all of L2 for 300 s (safe). Reads retry the delete. This meets R5 on the instance that made the change.

Notes:
1. **A timed-out write is not distrusted (fix, small).** `SetAsync` abandons a write after 250 ms, but the command may still reach Valkey later. If that late write carries a snapshot read before a state change, and the change's delete reached Valkey first, the stale copy then sits in L2 for up to 5 minutes. HybridCache has a narrower form of this race (read → write vs delete) by design, and the timeout widens it.
   - **Fix:** in `SetAsync`, when `ok` is false, call `RememberFailedRemoval(key)`. This is always safe: the next read is a miss and retries the delete.
   - Add a test: a slow Set that completes after a successful Remove must not be served.
2. **Lost-delete memory is per process (document it).** If an instance restarts within 5 minutes of a lost delete while Valkey is back, another instance's stale L2 copy can be read until it expires. That needs a Valkey outage plus a restart inside the 5-minute window.
   - Accept it as a residual risk.
   - Add a line in the RUNBOOK ("after a Valkey outage, flush the link snapshot keys or wait 5 min before restarting API instances").
   - Optionally, on startup, distrust L2 for `MaxEntryLifetimeSeconds` if the last shutdown was during an open circuit. Not required.

## Hub (PD-41)

- `Program.cs` no longer reads any token from the query string. Comments say `?access_token=` is ignored, and no `OnMessageReceived` remains.
- `/hubs/*` accepts:
  - a Bearer header (JWT);
  - `X-Api-Key`, now required to hold `stats:read` (`[Authorize(Policy = "scope:stats:read")]` on `AnalyticsHub`);
  - a single-use `?ticket=`. The hub-ticket principal has no `auth_method=api_key`, so it passes the scope policy, which is correct.
- The frontend test asserts that no `access_token` appears in the hub URL. Closes the round-2 "must before Stage 2" item.

## BE-002

- Every problem now has `type = https://zaa.gl/problems/<code>` (underscores written as hyphens) and a `code`. Framework-generated problems are normalised through `AddProblemDetails(CustomizeProblemDetails)`.
- No Phase 0 status changed. The Phase 0 code spellings (`not_found`, `refresh_raced`, …) are kept, and Phase 0 callers still return `validation` with an `idempotency-mismatch:` / `captcha-required` prefix in the message. I grepped every `new Error(` and `Error.Validation(` use.
- Note 3:
  - `ErrorCodes.IdempotencyMismatch` maps to **422** and `CaptchaRequired` to 400, but the Phase 0 link routes still send these as `validation` (400) with a message prefix. When BE-028 switches them to real codes, `idempotency-mismatch` moves from 400 to 422. Today's clients would see that change, so it needs an explicit decision in ARC-016 and a changelog line.
  - Align `ErrorCodes` with the architect's corrected catalogue (architect review, list A item 9).

## BE-004: path traversal

- Keys are created only from a server-generated `Guid` (`StorageKeys.Create`).
- Every key read back is checked by an anchored regex (`\A…\z`, lower-case hex, fixed areas, no dots or backslashes). The regex also requires the shard to equal the shard derived from the id, and `Create(area,id) == key` byte for byte.
- `PathFor` then checks `GetFullPath` stays under `Root + separator`.
- `GetInternalPath` refuses `temp` and `quarantine`, so they are never delivered.
- Writes go to `.partial` and are renamed atomically.
- `MoveAsync` is idempotent under concurrency. `OpenReadAsync` bounds range reads.
- I found no way for a request value to reach a filesystem path.

## BE-013: SKIP LOCKED claiming

- Claiming is correct:
  - The claim is a single statement: a CTE with `FOR UPDATE SKIP LOCKED LIMIT n`, then `UPDATE … FROM due RETURNING`. Two workers never get the same row, and expired leases are re-claimable.
  - Complete, fail and extend all require `locked_by = @instance AND status = 1`.
  - The instance id is `machine:pid:guid`.
  - Poison tasks (crash, then lease expiry) go dead once `attempts > max_attempts`, because the runner checks before running.
- Enqueue: `ON CONFLICT (dedupe_key) WHERE status IN (0,1)` matches the partial unique index exactly.
- Migration `AddTaskTable` creates the schema and table only. It is expand-only and is applied by the API's migrator, not the worker.
- Notes:
  4. **Fence by attempt, not only by instance.** If the lease extender stops (it gives up on the **first** exception, `TaskRunner.cs:225`), the lease expires after 300 s. The same process can then re-claim the task and run it twice. Both runs would pass `locked_by = @instance`, so the old run could complete or fail the new claim.
     - **Fix:** carry `attempts` from the claim and add `AND attempts = @attempts` to Complete/Fail/Extend.
     - Retry `ExtendLeaseAsync` on transient errors instead of stopping after one.
  5. **Dedupe while running.** A new enqueue is dropped while a task with the same key is *running*, so a change that happens after the handler read its state is lost. That is fine for idempotent "delete bytes of X" tasks, but it must be written into ARC-012 as a rule for handler authors: re-read state at the end, or use a key that includes a version.
  6. **Deployment.** The Worker has no Dockerfile or compose service yet. This goes to DevOps (also in the DevOps review).
- PD-25 (task tables vs Wolverine) and PD-15 (local disk vs R2) are still PROPOSED in DECISIONS.md, but PD-51 approved BE-004 and BE-013, which implement exactly these. Question U-1 asks the user to confirm.

## Process

- Stayed in backend-owned paths (`src/**`, `tests/**`, `src/Zaagl.Worker/**`). No commits or branches.
- The four `.claude/worktrees/agent-*` worktrees are at `833963a`, which predates this round. They were not created by this round. Their removal is question U-13.
