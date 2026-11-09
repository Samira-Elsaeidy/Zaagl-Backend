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

### Existing `coordstate` volume after the image UID change (one-time)

The API and coordinator images now run as fixed UID/GID `10001:10001` (previously a system user created by
`useradd -r`). A **new** `coordstate` volume takes `/state`'s ownership from the image, so nothing is needed.
An **existing** volume keeps the old owner, and the coordinator then cannot write
`/state/hwm.json` (`Coordinator__StateFilePath`). Fix it once, before starting the rebuilt coordinator:

```
docker compose stop coordinator
docker volume ls | grep coordstate          # the name is <compose project>_coordstate
docker run --rm -v <compose project>_coordstate:/state alpine chown -R 10001:10001 /state
docker compose up -d coordinator
```

On a dev machine, deleting the volume instead is also fine. **Never delete it on a host with real issued
codes** (it holds the allocator high-water mark; see "Database restore" below).

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

## Client IP: trusted proxies (spec §7.13)

The client IP (used by every IP limiter, guest quotas and `Admin:AllowedNetworks`) comes from
`X-Forwarded-For` only when the connection comes from a trusted hop:

| Setting | Value |
|---|---|
| `Network__KnownProxies__0`, `__1`, ... | Exact IPs of the reverse proxy and the web/BFF container on `edge` (fixed addresses). Never a subnet: a `/` fails startup. |
| `Network__KnownNetworks__0`, ... | CIDR ranges of the staging access gate. Staging only. |
| `Network__ForwardLimit` | `2` in production (proxy, web), `3` on staging (gate, proxy, web). Default `2`. |

`X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` are read right to left and stop at the first
untrusted address. **With both lists empty no forwarded header is trusted** (ASP.NET itself would trust
everyone in that state, so the API switches forwarding off): every request then counts against the address
of whatever connected to the API, so behind a proxy all visitors would share one rate-limit partition. Set the
proxy and BFF addresses before going live. An invalid entry fails startup.

## Rate limits (spec §7.3)

In memory per instance (a Valkey-backed limiter can replace `IRedirectRateLimiter` later). Every IP limiter
partitions by IPv4 address / IPv6 `/64`. Settings under `RateLimits` (per minute): `AuthPerMinute` (10),
`LinkWritePerMinute` (60), `UnlockPerMinute` (10), and `Redirects`: `PerMinute` (300), `MissesPerMinute`
(30 per `/64` or IPv4 address), `MissesPerMinutePer56` (120), `MissesPerMinutePer48` (300 per `/48` or IPv4
`/24`), `Enabled`. A partition over any redirect limit gets `429` with `Retry-After` on every `GET`/`HEAD
/{code}` until the minute ends. Paths of 1-3 characters or with several segments are `404` before any I/O.

## Valkey (cache L2) outage and API restarts

Each API instance remembers, in memory only, the link snapshot keys whose Valkey delete was lost or whose write
was abandoned during an outage, and does not read those keys from Valkey for `Cache:L2:MaxEntryLifetimeSeconds`
(300 s). That memory is per process: if an instance restarts within 5 minutes of a Valkey outage while Valkey is
back, another instance's stale copy of a just-changed link (for example a link that was disabled) can be read from
Valkey until it expires (at most 5 minutes). After a Valkey outage, either wait 5 minutes before restarting API
instances, or flush the link snapshot keys first: `valkey-cli --scan --pattern 'link:v2:*' | xargs -r valkey-cli del`
(add `-a "$VALKEY_PASSWORD"` when a password is set; the key prefix is `LinkCache.Key`, versioned `link:v2:`).

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

## Cloudflare edge: settings that must stay off (OPS-001)

The host, DNS, TLS, cache and WAF plan is `docs/phase1-implementation/design/ops-001-hosts-and-dns.md`. Changing
any of the following breaks the rule that no cookie is set on `usercontent.zaa.gl`, `zaa.gl` or `Domain=zaa.gl`
(addendum §4.2.2), or lets the edge alter or cache user bytes:

- Bot Fight Mode on.
- Any WAF or rate-limit action other than **Block**, such as a Managed or JS Challenge.
- I'm Under Attack mode.
- Turnstile pre-clearance.
- Always Online.
- Email Obfuscation, Rocket Loader or Automatic HTTPS Rewrites on the inert hosts.
- Removing the cache-bypass rule.
- Adding any DNS-only (grey-cloud) record that points at the VPS.

Do not make any of these changes without the product owner. After **any** Cloudflare change, re-run the cookie check
in step 9 of that document.
