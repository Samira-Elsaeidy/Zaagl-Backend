# Scaling to several API instances (spec §9)

Year 1 runs one `api` instance, one `coordinator`, one PostgreSQL, on one Docker host. This document
covers what's already in place for scaling out afterward, and what would still need building.

## Already supported by this build

1. **N `api` replicas behind a reverse proxy.** The app is stateless except for in-process caches
   (`HybridCache` for redirect snapshots, the guest rate limiter, the Web Risk lookup cache) which are
   per-instance by design - a cache miss on one instance just re-does the lookup, never an incorrect
   result. Each instance needs a unique `NODE_ID` (not currently read by any code path, but reserved by
   convention for future instance-identifying logging/metrics) and the proxy's exact address(es) in
   `KnownProxies` (never a whole subnet).
2. **Stateless JWT validation.** `JwtOptions.AllValidationKeys()` lets every instance validate every
   active signing key without any shared session state.
3. **Cross-instance cache invalidation.** `PgNotifyLinkCacheInvalidator` + `LinkCacheInvalidationListener`
   use Postgres `LISTEN`/`NOTIFY` so a link change on one instance invalidates every other instance's
   cached redirect snapshot, with a listener-liveness fallback (bypass the cache if an instance's listener
   has stalled, spec's R5 bound).
4. **Namespace separation is arithmetic.** The leased-range + keyed-scramble code generator guarantees two
   instances leasing from the coordinator concurrently never receive overlapping number ranges; alias
   ownership is the database's unique index plus a per-alias Postgres advisory lock, both already shared
   by every instance that connects to the same database. The key fingerprint check (`CreationGate`) means
   a misconfigured instance with the wrong scramble key refuses to create anything rather than silently
   producing codes nobody else can decode.
5. **The purge job, safety re-check job, and digest job already use `FOR UPDATE SKIP LOCKED`** batched
   claims (not a single-instance assumption) - running N instances of any of them splits the work instead
   of duplicating or racing it.

## Would need building before scaling out

1. **Guest rate limiting.** The per-minute token bucket and the global hourly ceiling
   (`GuestRateLimiter`) are explicitly in-memory and per-instance (matching the spec's own wording for
   those two specifically). With N instances, a guest's requests could be load-balanced across instances
   and exceed the intended per-minute/global limits by roughly a factor of N. Fix: move both counters to a
   shared store (Valkey, already in this stack for other purposes, is the natural choice) before running
   more than one instance.
2. **The Web Risk lookup cache and monthly budget.** The 24h per-URL cache is in-memory per instance (a
   cache miss on another instance just re-spends one budget unit - correct, just slightly less efficient).
   The monthly budget itself *is* already shared (one row in Postgres, incremented atomically), so this
   one is fine as-is; only the cache is a scaling-sensitivity note, not a correctness gap.
2. **The email digest job and dispatcher** poll on a timer per instance with no distributed lock. Running
   N instances means N instances all attempt the daily digest query and the 30-second dispatch claim - the
   dispatch claim is already `FOR UPDATE SKIP LOCKED`-safe (point 5 above), but the digest job's per-stage
   "queue once per user per day" relies only on the partial unique index to prevent duplicates, which is
   correct but means N-1 instances' attempts become wasted work (a caught `DbUpdateException`, not an
   error) rather than being avoided up front. Acceptable at small N; a `pg_try_advisory_lock` around the
   whole daily run (per spec §7.8's general job-locking guidance) would remove the wasted work.
3. **No read replica support for redirects.** Spec §9 permits serving redirects from read replicas in
   other regions under the "one write primary" topology; `RedirectsDataSource` currently always points at
   the single primary connection string. Splitting read/write connection strings for the redirect-specific
   data source would be the mechanism, with no change to the write path.
4. **The coordinator stays a single instance** by design (spec: "exactly one replica; never on the
   redirect path, never involved in aliases") - this is not a gap, just a reminder that "scaling out" means
   scaling `api`, never the coordinator itself.
5. **Admin's `Admin:AllowedNetworks` CIDR list** would need the real reverse-proxy/VPN ranges configured
   per environment before going multi-instance in a real network topology (today's dev default of an empty
   list, meaning "allow everything," must never ship to production regardless of instance count).

## Not attempted in this build

Horizontal scaling itself (actually running N `api` containers behind a load balancer and proving the
above holds under load) is not set up or tested - this build runs single-instance only, per year-1 scope.
