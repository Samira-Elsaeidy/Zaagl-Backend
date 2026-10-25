# Zaagl-Backend

ASP.NET Core 10 modular monolith + PostgreSQL 18 for **Zaagl** (zaa.gl). One deployable API, one worker, one tiny
coordinator service, one database with a schema per module.

## Layout
- `src/Zaagl.Api` - Minimal API host; one `Endpoints/<Module>Endpoints.cs` per module.
- `src/Zaagl.Coordinator` - Stand-alone service with one job: lease blocks of numbers for slug generation (see
  "Slug generation" below). Not a module - the API talks to it over HTTP, never a project reference.
- `src/Zaagl.Worker` - Worker Services (Wolverine handlers: analytics aggregation, expiry, media, scanning).
- `src/Zaagl.SharedKernel` - `IFileStorage`, the Feistel short-code scrambler and coordinator lease client, shared types.
- `src/Modules/<Name>` - Domain / Application / Infrastructure / Contracts. Other modules may only call `Contracts`.
- `tests/` - Links, Snippets, Files test projects (add more per module as they grow).

Modules: Identity, Organizations, Links, Resolution, Access, Analytics, Resources, Snippets, Files, Media, DeveloperTools, Collaboration, AI, Billing, Notifications, Administration.

## Running locally
```
docker compose up -d                          # PostgreSQL 18 + Valkey (dev only)
dotnet run --project src/Zaagl.Coordinator    # http://localhost:5085, leases number blocks for slugs
dotnet run --project src/Zaagl.Api            # http://localhost:5084, API docs at /scalar, health at /health
dotnet test
```
In Development the API applies every module's EF migrations at startup (`Database:MigrateOnStartup`).
Secrets and keys are optional locally; set them with `dotnet user-secrets` or environment variables
(`Stripe__SecretKey`, `WebRisk__ApiKey`, `Authentication__Google__ClientId`, `Jwt__Key` in production ...).
Each feature switches itself off cleanly when its key is missing (no Web Risk scan, no Stripe, no social login).
`ShortLinks__ScrambleKey` is the one exception: the API logs a loud warning and falls back to a random
per-run key if it is missing, since there is no slug generation at all without it (see "Slug generation").

## Phase 0 status (link engine)
Backend implemented: accounts (email, Google, GitHub, API keys), links (random + custom slugs, edit, folders,
tags, search, CSV bulk import up to 500), redirect with HybridCache, access gates (password, expiry, click limit,
fallback URL), routing (device, country, A/B split), Open Graph previews for link-preview crawlers, analytics
(buffered writes, bot filtering, live SignalR feed, public stats), Stripe billing (Free vs Starter), Web Risk
scanning, rate limiting.
Not yet: frontend screens, browser extension, QR/UTM (frontend work), Wolverine/worker jobs.

### Slug generation ("Strategy 6": leased ranges + keyed scramble)
`Zaagl.Coordinator` holds one number, `next_start`, in its own `coordinator.lease_state` table. The API leases a
block of 10,000 numbers at a time (`CoordinatorLeaseClient`, one HTTP call per 10,000 generated slugs, not per
slug) and counts through its leased block in memory. Each leased number is then run through a keyed 4-round
Feistel network (`FeistelScrambler`) over a 40-bit space and written in base 62, so consecutive leased numbers
never produce consecutive-looking codes. Uniqueness comes from the lease itself (no two callers are ever handed
the same number); the database's unique index on `slug_key` is the actual backstop for any clash with a
reserved word or a hand-picked custom slug, exactly as it already was before this change.

`ShortLinks:ScrambleKey` must never change once any codes have been issued - a new key re-shuffles the entire
number space, so an already-issued code could map to a different number than before. Store it like a password
(`dotnet user-secrets` or an environment variable), never in source control.

The coordinator writes its own high-water mark to a local `coordinator-state.json` file before answering every
lease (write-ahead), and refuses to start if the database's `next_start` is ever behind what that file last
recorded - the sign of a database restored to a point before numbers already leased from here. If that happens,
the fix is manual: compare the file to `coordinator.lease_state` and decide by hand which one is trustworthy.

Design decisions worth knowing:
- Redirects are 302 with `Cache-Control: no-store`, so edits apply at once and every click reaches analytics.
- Click limits are enforced by one atomic conditional `UPDATE` in PostgreSQL, never by the cache.
- `analytics.click_events` is range-partitioned by month (hand-written migration). `ensure_click_partitions()` keeps
  upcoming partitions in place; the API calls it every 12 hours. Dashboards query the raw events for now;
  hourly/daily aggregate tables are not built yet.
- Unique visitors use a daily-rotating hash of IP + user agent; no IP address is stored.
- Plan limits live in `PlanLimits.For(...)` (Billing): Free = 50 links/month, random slugs only, 7-day analytics,
  date expiry only; Starter = unlimited, custom slugs, 1-year analytics, password / click limits / routing.
- API routes live under `/api`; the only top-level route is the catch-all `/{slug}` (Resolution), which is why
  `SlugRules` reserves names like `api`, `health`, `unlock`.

## Notes
- Remote is still `Zagel-Backend`; rename the GitHub repo when ready.
- Files live on local disk behind `IFileStorage`, served via Nginx `X-Accel-Redirect` after authorization.
- Strict counters (one-time access, click limits) stay authoritative in PostgreSQL, not Valkey.
