# Runbook

Operational procedures for Zaagl's backend. For what's built vs. not, see `docs/TRACEABILITY.md`; for why
things were built the way they were, see `docs/DECISIONS.md`.

## Starting the stack (dev/demo)

```
docker compose build api migrator coordinator
docker compose run --rm migrator       # runs every module's EF migrations, then exits
docker compose up -d postgres valkey coordinator api
```

Health checks: `GET /health/live` (process up), `GET /health/ready` (both data sources reachable - never
depends on the coordinator), `GET /health/issuer` (`KeyUnverified`/`KeyMismatch`/`Waiting`/`Ok`/`Faulted` -
whether generated/alias creation is currently gated open or closed).

The coordinator's own status: `GET http://coordinator:8080/v1/status` (or via the admin proxy,
`GET /api/admin/allocator`, Admin-only).

## First-time coordinator initialisation

A fresh `alloc` schema has no installation row. `POST /v1/admin/init` (bearer `Coordinator:AdminToken`)
creates it, recording the current key fingerprint (`KeyFingerprint.Compute(ShortLinks:ScrambleKey)`).
**This can only be done once per key** - the fingerprint is permanent for the life of that scramble key
(spec §5.3: never rotate it once codes exist). Confirm success via `/v1/status`'s `leasing: "ok"`.

## Key fingerprint mismatch (`KeyMismatch`)

`/health/issuer` shows `KeyMismatch` when the API's `ShortLinks:ScrambleKey` doesn't match what the
coordinator has on record. This means either: the wrong key is configured on this instance (fix the
config, don't touch the coordinator), or the coordinator's `alloc.installation` row is stale/wrong (if you
are certain no codes have been issued under the currently-configured key, see "Dev-only state reset"
below - **never** do this against a database with real issued codes; it will silently violate R2, "a
generated code can never become an alias").

## Dev-only state reset (never run against a database with real users or links)

If `alloc.installation`/`alloc.leases`/`alloc.pools`/`alloc.reservations` hold leftover test fixtures (a
recognisable symptom: `key_fingerprint` is an obviously-fake value like `"test-fingerprint"`, or
`next_offset` is nonzero with no corresponding real links), clear them and re-init:

```sql
DELETE FROM alloc.reservations;
DELETE FROM alloc.leases;
DELETE FROM alloc.pool_ranges;
DELETE FROM alloc.pools;
DELETE FROM alloc.installation;
DELETE FROM alloc.recovery_log;
```

Restart the coordinator (it will report `not-initialised`), then `POST /v1/admin/init` again. Expect the
very first few link creations after this to retry once or twice (`409`) if any numbers near the new
starting offset were already consumed by earlier dev activity - this is the designed self-healing retry
loop (`LinkService.CreateAsync`'s collision loop, spec §7.2 step 11), not a bug.

## Database restore (spec §8.1)

1. Stop `api` (all replicas), then `coordinator`.
2. Restore PostgreSQL from backup. **Do not touch the `coordstate` volume.**
3. Declare the alias-restore freeze (mandatory, independent of the automatic heuristic - this build does
   not have the automatic coordinator-driven sync job, see `docs/DECISIONS.md`, so this step is the *only*
   source of the freeze):
   `POST /api/admin/declare-restore { "reason": "<incident>" }` (Admin-only; needs an already-running API,
   so do this once `api` is back up in step 5, before resuming normal traffic acceptance for creation).
4. Start `coordinator`. It runs its startup check: moves the allocator to the HWM if the restore is
   recent/small enough (row 3), or blocks leasing for an operator decision (rows 5-7) - see
   `src/Zaagl.Coordinator/StartupCheck.cs` and `RecoveryService.cs` for the exact conditions.
5. Start `api`. Check `/health/issuer` (`Ok`) and `GET /api/admin/allocator` (`leasing: "ok"`).
6. Run the alias-freeze declare from step 3 now if not already done.

**Host lost entirely**: provision a new host, restore secrets from escrow (scramble key, JWT signing
keys, `GUEST_KEY`, DB credentials), restore PostgreSQL from the latest base backup plus every archived WAL
segment, then follow steps 3-6 above. The coordinator's recovery modes (`reattach`, `trust-database`,
`resume`, `new-length`) are implemented in `RecoveryService.cs`; this build's startup check selects among
them automatically based on what it finds, per `docs/spec/zaagl-spec-v9.4.md` §6.4 - there is no `ops`
CLI container to invoke a mode manually in this build (a documented deviation, see `docs/DECISIONS.md`).

## Admin bootstrap

Set `AdminBootstrap:Email` and `AdminBootstrap:Password` before first start; `AdminBootstrapHostedService`
creates that account with role `Admin` once, at startup, if no admin exists yet. First login as that
admin returns `{ enrolToken }` (no TOTP enrolled yet) - use it against `POST /api/auth/totp/enroll` then
`/totp/confirm` to finish setup before any `/api/admin/*` route becomes reachable (the `Admin` policy
requires both the role claim and a session issued after TOTP passed).

## Admin network restriction

`Admin:AllowedNetworks` (CIDR list) gates every `/api/admin/*` route in addition to the role+TOTP check.
An empty list (this build's dev default) allows every address - **production must configure real ranges**,
since an empty list there defeats the restriction entirely.

## Abuse reports and takedowns

See `docs/ABUSE.md` for the response-time targets and the moderation workflow itself.

## Rotating secrets

- **`ShortLinks:ScrambleKey`**: never, once any code has been issued under it (spec §5.3) - a rotation
  changes what every existing generated code decodes to.
- **`GUEST_KEY`**: safe to rotate any time; only resets guest quotas, never affects already-issued links.
- **`JWT_SIGNING_KEYS`**: add the new key as the last entry (it starts signing); keep the previous entry
  for validation-only overlap until every outstanding access token (15-minute lifetime) has expired, then
  remove it.
- **`Coordinator:Token` / `Coordinator:AdminToken`**: rotate by updating both the coordinator's and the
  API's configuration together and restarting both; there's no overlap window, so expect a brief gap in
  generated-link creation during the restart (aliases, renewals and redirects are unaffected).

## Quarterly restore drill

Run the restore procedure above end-to-end on an isolated copy (separate WAL archive, separate allocator
sink credentials - the drill must never write to production's archive or sink). Confirm sample codes
resolve correctly afterward, and record the time taken as the measured RTO. **Not automated in this
build** - there is no `scripts/chaos.sh` or drill harness yet (see `docs/TRACEABILITY.md`).
