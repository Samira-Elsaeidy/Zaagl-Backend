# ARC-012 · Job hosting and job catalogue

| | |
|---|---|
| Owner | zaagl-architect |
| Status | DESIGN. Hosting model APPROVED (PD-25, 2026-11-09): PostgreSQL task tables claimed with SKIP LOCKED in `Zaagl.Worker`, as built in BE-013. |
| Related | C10 lifecycle outbox (`src/Modules/Links/Contracts/ContentLinkContracts.cs:78-115`), C2 quota and grants, C7 storage deletion outbox (`IStoredObjects.QueueDeletionAsync`), addendum §2.2, §7.1, §8.2, §9, §13; ARC-006 (state machine), ARC-008 (burn grace) |

## 1. Hosting model (PD-25, APPROVED)

- Background work for Phase 1 content runs as **PostgreSQL task tables claimed with `FOR UPDATE SKIP LOCKED`**,
  hosted in **`Zaagl.Worker`** (a separate container). This is the pattern Phase 0 already uses in three places
  (`src/Modules/Links/Application/PurgeService.cs:70-80`, `src/Modules/Links/Application/AbuseControls.cs:164-174`,
  `src/Modules/Identity/Application/EmailJobs.cs:66-80`, the last one with a `locked_until` lease).
- **Wolverine is deferred.** The tech stack names it (`Zaagl-Frontend/src/Doc/zaagl-tech-stack (2).html`, lines 277,
  394, 543), but nothing uses it today and it would add a second job model next to the existing one. Revisit when a
  real message bus (multiple hosts, sagas) is needed.

## 2. Inventory of today's background jobs

| Job | Location | Registered | Kind | Stage 1 placement |
|---|---|---|---|---|
| `PurgeJob` | `src/Modules/Links/Application/PurgeService.cs:26` | `LinksModule.cs:106` | scheduled batch, SKIP LOCKED | stays in API |
| `GuestQuotaCleanupJob` | `src/Modules/Links/Application/GuestCreation.cs:132` | `LinksModule.cs:107` | scheduled cleanup | stays in API |
| `SafetyRecheckJob` | `src/Modules/Links/Application/AbuseControls.cs:135` | `LinksModule.cs:116` | scheduled batch, SKIP LOCKED | stays in API |
| `LinkCacheInvalidationListener` | `src/Modules/Links/Infrastructure/LinkCacheInvalidation.cs:80` | `LinksModule.cs:54` | per-instance `LISTEN` | **always in every API instance** |
| `CreationGatePoller` | `src/Zaagl.SharedKernel/Coordination/CreationGatePoller.cs:10` | `LinksModule.cs:98` | per-instance state | **always in every API instance** |
| `EmailDispatcher` | `src/Modules/Identity/Application/EmailJobs.cs:40` | `IdentityModule.cs:43` | outbox drain with lease | stays in API |
| `EmailDigestJob` | `src/Modules/Identity/Application/EmailJobs.cs:214` | `IdentityModule.cs:44` | scheduled | stays in API |
| `AdminBootstrapHostedService` | `src/Modules/Identity/Application/BackgroundJobs.cs:14` | `IdentityModule.cs:48` | startup, once | stays in API |
| `UnconfirmedAccountCleanupJob` | `src/Modules/Identity/Application/BackgroundJobs.cs:48` | `IdentityModule.cs:49` | scheduled cleanup | stays in API |
| `ClickWriterService` | `src/Modules/Analytics/Infrastructure/ClickPipeline.cs:98` | `AnalyticsModule.cs:32` | per-instance in-memory buffer | **always in every API instance** |
| `PartitionMaintenanceService` | `src/Modules/Analytics/Infrastructure/ClickPipeline.cs:254` | `AnalyticsModule.cs:33` | scheduled DDL | stays in API |
| `MigrationHostedService` | `src/Zaagl.Api/Program.cs:212` | API host | startup | stays in API |
| `Worker` (template loop) | `src/Zaagl.Worker/Worker.cs:3` | `src/Zaagl.Worker/Program.cs:4` | none (logs every second) | replaced by the job runner |

Rules:
- Jobs holding **per-instance state** (cache invalidation listener, click buffer, creation gate) stay in each API
  instance forever; moving them would break their purpose.
- Phase 0 scheduled jobs **do not move in Stage 1**: they work, are SKIP LOCKED safe for several instances, and moving
  them is risk without benefit. They may move later when the worker is proven (each move is its own task).
- New Phase 1 content jobs (§4) live in the worker only. They do CPU and disk work (hashing, ClamAV, libvips, FFmpeg)
  that must not compete with redirects.

## 3. Generic task table

One table per owning module schema (module owns its data), same shape everywhere:

```sql
CREATE TABLE <schema>.tasks (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    kind          text        NOT NULL,                 -- e.g. 'scan', 'promote', 'delete-bytes'
    dedupe_key    text        NOT NULL,                 -- e.g. 'scan:<resourceId>'
    payload       jsonb       NOT NULL DEFAULT '{}',    -- ids only; never a link key, password or ticket
    status        smallint    NOT NULL DEFAULT 0,       -- 0 pending, 1 running, 2 done, 3 dead
    run_after     timestamptz NOT NULL DEFAULT now(),
    attempts      int         NOT NULL DEFAULT 0,
    max_attempts  int         NOT NULL,
    locked_until  timestamptz NULL,                     -- lease
    locked_by     text        NULL,                     -- worker instance id, for diagnostics
    last_error    text        NULL,                     -- truncated to 2,000 chars, no secrets
    created_at    timestamptz NOT NULL DEFAULT now(),
    completed_at  timestamptz NULL
);
CREATE UNIQUE INDEX CONCURRENTLY tasks_dedupe_open ON <schema>.tasks (dedupe_key) WHERE status IN (0, 1);
CREATE INDEX CONCURRENTLY tasks_due ON <schema>.tasks (run_after) WHERE status IN (0, 1);
```

Tables: `files.tasks` (scan, promote, delete-bytes, quarantine, upload expiry), `media.tasks` (previews),
`resources.tasks` (burn finaliser, quota reconciliation, orphan sweep). The Links lifecycle outbox
(`links.lifecycle_outbox`, C10) and the bytes-deletion outbox (`files.storage_deletions`, C7) keep the shapes their
contracts define but use the same claim, lease and retry rules.

**Enqueue** in the same transaction as the state change that causes it (`INSERT ... ON CONFLICT (dedupe_key) WHERE
status IN (0,1) DO NOTHING`), so a crash can never lose or duplicate work.

**Claim** (short transaction, then work outside it):

```sql
WITH due AS (
  SELECT id FROM <schema>.tasks
  WHERE kind = ANY(@kinds)
    AND ((status = 0 AND run_after <= now()) OR (status = 1 AND locked_until < now()))
  ORDER BY run_after, id
  LIMIT @batch
  FOR UPDATE SKIP LOCKED)
UPDATE <schema>.tasks t
SET status = 1, attempts = t.attempts + 1, locked_until = now() + @lease, locked_by = @instance
FROM due WHERE t.id = due.id
RETURNING t.*;
```

Time in application code comes from `TimeProvider`; the SQL above may use `now()` only because it is the database's
clock on both sides of the comparison (BE may pass the time as a parameter instead for testability).

**Complete**: `status = 2, completed_at = now(), locked_until = NULL` with `WHERE id = @id AND locked_by = @instance`
(a task whose lease expired and was re-claimed is not completed twice). **Fail**: `status = 0, run_after = now() +
backoff(attempts), last_error = ...`; when `attempts >= max_attempts` → `status = 3` (dead) and the job's own
failure behaviour (§4) runs. **Lease**: 5 minutes default, longer for scan/preview (`Jobs:<kind>:LeaseSeconds`); a
long task extends its lease every lease/3.

**Idempotency**: every handler re-reads current state and does nothing if the effect already happened (file already
promoted, bytes already gone, quota already released per resource). Deleting a missing key is success
(`IObjectStorage.DeleteAsync` contract). At-least-once delivery is assumed everywhere.

**Poison handling**: dead tasks stay (no auto-delete for 30 days), raise a metric and an alert; an admin can requeue
(`status = 0, attempts = 0`) through a runbook SQL until an admin UI exists.

**Retention**: done rows deleted after 7 days by the runner itself.

**Metrics** (OpenTelemetry, DEVOPS_PLAN §6): per kind queue depth, oldest due age, run duration, attempts, dead count;
alerts on oldest due age > 15 min for `delete-bytes` (addendum §9.6) and any dead `scan`.

## 4. Phase 1 job catalogue

Status column: **D** = behaviour decided by an approved decision or the addendum; **P** = depends on a PROPOSED item;
**O** = depends on an OPEN item (do not implement that part).

| Job (kind) | Owner | Trigger / schedule | Concurrency | Dedupe key | Failure behaviour | Status |
|---|---|---|---|---|---|---|
| Lifecycle outbox drain | Links (outbox) → Resources (`ILinkLifecycleHandler`) | poll every 2 s | 1 batch of 100 per instance, ordered by id per link | outbox `id` | retry 1, 5, 15, 60 min, then dead + alert; handler idempotent | D (C10) |
| `LinkExpired` emitter | Links | every 1 min: content links with `expires_at <= now` and no `LinkExpired` row | 1 | `expired:<linkId>:<expires_at>` | retry next tick | D (C10) |
| `scan` | Files | enqueued at upload complete | `Scan:Concurrency` (proposed 2) | `scan:<resourceId>` | `Scan:RetryMinutes` 1, 5, 15, 60, max `Scan:MaxAttempts` 6, then `MarkRejectedAsync(scan-unavailable)`; never served (fail-closed) | D (addendum §7.1) |
| `promote` | Files | after a clean verdict, same task or follow-up | 4 | `promote:<resourceId>` | atomic rename temp → objects; retry; then Error | D |
| infected / blocked hash | Files → Links (`ILinkTakedown`, safety/abuse) | from `scan` / hash check | inline in scan task | `takedown:<resourceId>` | retry; evidence row required (MUST NOT 18) | D (§13) |
| `media-preview` | Media | after `ready` for media kinds | `Media:Concurrency` (proposed 2, CPU-bound) | `preview:<resourceId>:<derivative>` | retry 3, then no preview (original still served); never for burn files | P (PD-30 transcoding, PD-18 SVG); thumbnails D |
| Pending-resource orphan sweeper | Resources | every 15 min | 1 | none (set query) | releases reservation; deletes resource rows `pending` > 24 h with no link | D (addendum §2.2.4) |
| Upload-session expiry | Files | every 15 min | 1 | none | idle > 24 h: delete temp bytes, release reservation, status Expired | D (addendum §6.4) |
| `delete-bytes` (storage deletion outbox) | Files | enqueued by purge (`purge_after`, which includes owner-deleted content, PD-55), guest expiry (`expires_at`), burn finaliser | 4 | `delete:<storageKey>` | retry with backoff; alert if still present 15 min after its time | D (owner delete at `purge_after`, PD-55) |
| Burn finaliser | Resources | every 1 min: claimed grants past `Burn:GraceMinutes` or `Burn:MaxRequests` | 1 | `burn-final:<resourceId>` | enqueue `delete-bytes`, release quota; idempotent | D (PD-05) |
| Quota reconciliation | Resources | daily 03:00 UTC | 1 | `reconcile:<date>` | recompute `used_bytes = Σ quota_usage.bytes WHERE released_at IS NULL` and `reserved_bytes = Σ open reservations` per owner under the advisory lock (ARC-006 §4); log and fix drift; alert if drift > 0 | D (technical) |
| Legal-hold release | Files | daily | 1 | none | delete quarantined bytes whose `legal_hold_until` passed and no hold extension; evidence rows kept | D (§13); legal duties O (PD-11) |
| Content hash blocklist sweep | Files → Links | on admin add of a hash (enqueue), plus daily safety pass | 1 | `hash-sweep:<sha256hex>` | take down live resources with that hash (abuse) | D (addendum §13.4) |
| Done-row cleanup | runner | daily | 1 | none | delete `status = 2` older than 7 days | D (technical) |

Not jobs in Phase 1: Web Risk on URLs inside snippets (addendum §13.5, not required), EXIF stripping (PD-13 OPEN),
owner email on burn (OPEN), CSAM hash matching (PD-11 OPEN).

## 5. Redirect priority (tech stack §11)

- The worker is a **separate container** with its own CPU and memory limits (proposed `cpus: 4`, `mem_limit: 6g` on the
  12–16 vCPU host; DEVOPS_PLAN §2 sizes the host). The API container keeps priority; worker child processes (clamd is
  its own container; libvips/FFmpeg run as child processes) inherit the worker's cgroup limit.
- The worker uses its **own PostgreSQL pool** (`Jobs` connection string, max 10 connections) and never the redirect
  pool (`RedirectsDataSource`, `src/Modules/Links/Application/LinkResolver.cs:55-60`).
- Each job kind has a concurrency cap (§4) and runs at `nice 10` for child processes.
- Every write transaction sets `SET LOCAL lock_timeout = '3s'` like the API (spec §7.0) and processes rows in chunks of
  at most 1,000.

## 6. Worker hosting outline

- `Zaagl.Worker/Program.cs` builds a generic host, reads the same configuration sections as the API, and calls each
  module's existing registration method for **services only**, with a flag that skips hosted services and endpoints:
  e.g. `services.AddLinksModule(config, hostRole: HostRole.Worker)`. BE adds the `HostRole` switch to the module
  `Add*Module` methods (`LinksModule.cs`, `IdentityModule.cs`, `AnalyticsModule.cs`, plus the new Resources, Files,
  Media modules) so the worker registers no Phase 0 hosted service and the API registers no content job.
- One `TaskRunner<TDbContext>` hosted service per task table, configured with the kinds it handles; handlers implement
  an internal `ITaskHandler { string Kind; Task HandleAsync(TaskRow, CancellationToken) }` inside the owning module
  (not a contract: only the module's own runner calls it).
- The worker exposes no public endpoint. Health: a liveness file or a loopback-only `/health` on an internal port
  checked by Docker `healthcheck`; readiness = DB reachable and ClamAV `PING` answered.
- Graceful shutdown: on SIGTERM stop claiming, let running tasks finish within `HostOptions.ShutdownTimeout = 60s`,
  then abandon; the lease expiry returns unfinished tasks to the queue. No task relies on finishing.
- Migrations stay in the API's `MigrationHostedService`; the worker waits for the schema version it needs before
  claiming (readiness).
- Scaling: several worker replicas are safe by construction (SKIP LOCKED + leases); start with one.

## 7. Decided vs proposed vs open

| Item | Status |
|---|---|
| Task tables + SKIP LOCKED in `Zaagl.Worker`, Wolverine deferred | APPROVED (PD-25) |
| Phase 0 jobs stay in the API in Stage 1; per-instance jobs stay forever | architect recommendation (technical) |
| Scan retry schedule, burn grace, quarantine, guest deletion at expiry | DECIDED (addendum §7.1, §8.2, §9) |
| Byte deletion at owner delete | at `purge_after` (PD-55 APPROVED) |
| Video transcoding, SVG rasterising | PROPOSED (PD-30, PD-18) |
| Legal hold duration and backups | OPEN (PD-11) |
| Worker CPU/memory limits | proposed values, OPS to size (PD-27 procurement OPEN) |

## 8. Open questions for the user

None. PD-25 (job model) and PD-55 (owner delete keeps bytes until `purge_after`) are approved.

## 9. Review checklist for BE / OPS

- [ ] Every enqueue happens in the same transaction as its cause; dedupe index prevents duplicates.
- [ ] Claim uses SKIP LOCKED + lease; complete checks `locked_by`; expired leases are re-claimed.
- [ ] Each handler is idempotent (run twice → same result) with a test.
- [ ] Payloads hold ids only: no link key, password, grant or ticket (MUST NOT 7).
- [ ] Scan failure path ends in `rejected/scan-unavailable`, never `ready`.
- [ ] Worker container has CPU/memory limits and its own DB pool; API redirect p95 unchanged under a scan burst.
- [ ] Dead-task and oldest-due-age alerts wired; `delete-bytes` 15-minute lag alert (addendum §9.6).
- [ ] Phase 0 hosted services are not registered twice (API and worker).
