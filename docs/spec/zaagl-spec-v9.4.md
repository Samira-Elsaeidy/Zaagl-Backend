# Build prompt: Zaagl short-link service (.NET, PostgreSQL, EF Core, leased ranges + keyed scramble)

**Version 9.4 — frozen for implementation.** 9.3 made API keys premium-only (Appendix K); 9.4 fixes the findings of a third external review (Appendix L). Revised after eight external reviews, a seven-discipline review (architecture, product, security, trust and safety, operations, database, QA), a second external multi-discipline review, and product-owner decisions. 9.2 adds abuse controls, the host layout, the web app contract, allocator recovery without whole-life evidence, key escrow, account deletion and export, and deterministic test seams. Changes are listed in Appendices A–L (one per revision). Further changes come from implementation findings and failing tests, not from design review.

You are a senior .NET engineer. Build a production-quality short-link service in C# with ASP.NET Core Web API, PostgreSQL and Entity Framework Core, packaged with Docker Compose. Code generation **must** use the "leased ranges + keyed scramble" strategy described below. Read the whole prompt before writing code. A rule marked **MUST** is not negotiable. If something is still ambiguous, pick the safer option and record it in `docs/DECISIONS.md`; do not silently invent behaviour.

---

## 1. Context and goals

- Product: **Zaagl** (`zaa.gl`), operated by **TechTroll** (Egypt), production hosted in the **EU**. This build covers **short links only**; files, images and snippets come later under short path prefixes on `zaa.gl` (§4.5).
- Year 1: **one API instance**, one coordinator, one PostgreSQL, all on one Docker host, behind a reverse proxy that terminates TLS (the proxy itself is configured in the infra repository; this build defines what the API expects from it, §7.13).
- After year 1: **several API instances** behind a load balancer, with **no redesign of the core and no data migration**. Scaling may add *new implementations of interfaces that already exist* and configuration; it must not change the schema, the allocator protocol or the code format. The supported topology is **one write primary**; other regions may serve redirects from read replicas (§9).
- The system **must recover safely** from a crash or restart of any container at any moment (including `kill -9` and host power loss), from a PostgreSQL restore, and from **loss of the whole host** (restore from backup onto a new host), within the guarantees stated in §8.
- Codes start at **5 characters** and grow to 6, 7, … 10 as each length reaches its fill limit. Codes are **not guessable by counting**, but they are **not secrets**: 5 Base62 characters are about 30 bits. Never treat a short code as an access token (§5.3).

### 1.1 Product rules (decided by the product owner)
All durations are **fixed numbers of days** (UTC), never calendar months. All values are configuration (`Plans:*`, `Links:*`, `Aliases:*`).

| | Guest (no account) | Free | Premium |
|---|---|---|---|
| Create generated links and custom aliases | ✓ | ✓ | ✓ |
| Links per rolling 24 h (aliases included) | 20 per IP partition | 1,000 per user | 1,000 per user |
| Alias limits | within the 20 per day | 500 active, 50 new per day | same |
| Default expiry | 7 days | 30 days | 365 days |
| Maximum expiry (from creation or renewal) | 7 days | 90 days | 1,095 days |
| Quarantine after expiry | **none** (purged at expiry) | 90 days | 90 days |
| Renew, change destination, delete | ✗ | ✓ | ✓ |
| API keys | ✗ | ✗ | ✓ (up to 20) |
| Click statistics | **none** (only an aggregate click counter, for abuse detection, §7.3) | live links; daily totals deleted with the link at purge | live links; daily totals **kept after purge until the owner deletes the account** |
| Emails | none | per §7.10 | per §7.10 |
| Account deletion and data export | — | self-service (§7.14) | self-service (§7.14) |

- **Every link expires**, generated or custom alias.
- **Guests** (requests without authentication) may create links (§7.12). A guest link cannot be managed by anyone (no renewal, edit, delete, statistics or emails), except that an admin can view and disable it. Guest links have no quarantine: at `expires_at` they stop resolving (`404`) and are purged; a guest alias is then immediately available to anyone. Accepted product risk.
- After expiry every **account-owned** link enters a **quarantine** of 90 days (`Links:QuarantineDays`, minimum 30). During quarantine the link answers `410` and its owner can renew it (or upgrade and renew for longer). After quarantine the link is **purged** (deleted).
- **Renewal only moves expiry forward.**
- **Email addresses are confirmed** by a link sent at registration. **An unconfirmed account cannot sign in** and has no links, keys or data beyond the registration itself. Unconfirmed accounts are deleted after `Accounts:UnconfirmedTtlDays` (7). Registering an address again while it is unconfirmed restarts the registration (§7.4), so nobody can squat someone else's address.
- **Users are warned by email** before expiry and before purge (§7.10). Warnings are **best effort with retries**. **Purge always happens on time** at `purge_after`, on every path; nothing waits for an email.
- Registration requires accepting the current terms of service and acceptable use policy; the accepted version and time are stored. Guest creation states that the acceptable use policy applies.
- **Abuse controls ship at launch** (§7.15): destination reputation check on create and edit plus periodic re-checks, a destination blocklist, CAPTCHA on guest creation and registration, bulk takedown, a public report channel and an alias denylist. Reports are handled by the TechTroll team within **48 hours** (runbook).
- **Users can delete their account and export their data** themselves (§7.14).
- **Generated codes are never reused**, even after purge.
- **Custom aliases accept any format** allowed by §4.5 (including its denylist). When an alias's quarantine ends it is **released** and anyone may claim it. Accepted product risk, re-confirmed in v9.2 after two reviews argued against it: people holding the old link reach the new owner's destination. **Exception:** an alias whose link was taken down for abuse or by the safety check is **never released** (§7.15).
- **After a database restore, new alias claims are frozen for 30 days** (`Aliases:RestoreFreezeDays`), so an alias lost in the restore cannot be taken by someone else before its owner notices (§7.11). Accepted cost: nobody can create new aliases during the freeze.
- **No alias can ever take over a code that was generated**, and **no generated code can ever equal an alias** (§4.5).
- **Disabling a user**: the admin chooses to disable the account only, or the account and all its links (§7.4).
- Plans are set by an admin in this build (billing is out of scope).

### 1.2 Hosts
| Host | Serves | Notes |
|---|---|---|
| `zaa.gl` | `GET/HEAD /{code}`, the 404/410/policy pages for codes, `/.well-known/security.txt` | **Never sets a cookie.** Every other path returns `404`. Paths of 1–3 characters and multi-segment paths are reserved for future products (§4.5). |
| `api.zaa.gl` | `/api/v1/*`, `/openapi/v1.json`, the account pages (`/account/confirm-email`, `/account/reset-password`), `/health/*` (internal only, §7.7) | Bearer tokens and API keys only; no cookies; no CORS. |
| `app.zaa.gl` | The web app and its server-side BFF (separate repository, §7.13) | Host-only `__Host-` cookies. |
| `notify.zaa.gl` | Email sending domain (From, Return-Path, DKIM) | SPF, DKIM, DMARC `p=quarantine` or stricter. |
| `staging.zaa.gl`, `api.staging.zaa.gl`, `app.staging.zaa.gl` | Staging copies of the above | Behind an access gate; own secrets; `noindex` (§3). |

The API process serves both `zaa.gl` and `api.zaa.gl` and **routes by host**: redirect endpoints exist only on `Hosts:Short`, API and account endpoints only on `Hosts:Api`; any other host gets `421`. `/health/*` is exempt from host routing (Docker and internal monitors call it by container name). The BFF calls the API on the internal network with `X-Forwarded-Host: api.zaa.gl`, honoured only from `KnownProxies` (§7.13). A spare domain is registered and parked (runbook) as an emergency fallback for the app and API hosts.

### Non-goals for this build
- Multi-region active-active writes. Alias ownership depends on one database; other regions may only add read replicas (§9).
- Billing and plan purchase. Branded (customer) short domains: product decision, a schema migration later is accepted (`docs/DECISIONS.md`).
- Backups, TLS termination, the reverse proxy configuration, log shipping and monitoring: built in the infra repository, not here. This build **must** provide what they need (§3, §6.4, §7.13) and the runbook steps.
- Scramble-key rotation (§5.3). Key **escrow** is required (§5.3).
- A legal retention log of creator IP addresses (product decision; open legal question in `docs/DECISIONS.md`, §11 #26).
- The web UI itself (separate repository `zaagl-web`). This build defines the contract the web app relies on (§7.13).

---

## 2. Tech stack

- .NET 10 (LTS), ASP.NET Core Minimal APIs grouped by feature (`MapGroup`), nullable enabled, warnings as errors, `checked` arithmetic in allocator and permutation code.
- EF Core 10 with Npgsql provider, `EFCore.NamingConventions` (snake_case), PostgreSQL 17.
- ASP.NET Core Identity (EF stores) for users, password hashing, email confirmation and password-reset tokens; JWT bearer access tokens issued by the API; refresh tokens stored hashed.
- `System.Threading.RateLimiting`, `IMemoryCache` (with a size limit, §7.3), `System.Threading.Channels`, `BackgroundService`.
- Email: MailKit over SMTP to an external provider, behind `IEmailSender`; a transactional outbox (§7.10).
- OpenTelemetry (metrics + traces), structured JSON console logs.
- Tests: xUnit, FluentAssertions (or Shouldly), Testcontainers for PostgreSQL, `WebApplicationFactory`, a fake clock (`TimeProvider`, §8.3), Mailpit, NetArchTest (architecture rules), Stryker.NET (mutation testing of the core, §12).
- `Microsoft.CodeAnalysis.BannedApiAnalyzers` with a committed `BannedSymbols.txt`: `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `System.Random`, `RelationalDatabaseFacadeExtensions.SqlQuery*`, `DatabaseFacade.EnsureCreated*`. Violations fail the build.
- Abuse controls: Google Web Risk Lookup API (`uris.search`) behind `IUrlSafetyCheck`; Cloudflare Turnstile behind `ICaptchaVerifier`. Both are HTTP lookups; the service **never fetches a destination URL**.
- Admin MFA: ASP.NET Core Identity authenticator (TOTP).
- No third-party library for the scramble: implement it exactly as specified in §5.

All code reads time from an injected `TimeProvider` (UTC). SQL that compares times uses a parameter `@now` taken from the same `TimeProvider`, never `now()` inside SQL. Timeouts, retry backoff and token buckets run on real time and are configurable, so tests can shrink them (§8.3).

---

## 3. Containers (Docker Compose)

| Service | Image / project | Exposed | Notes |
|---|---|---|---|
| `postgres` | `postgres:17` | internal only | Volume `pgdata`. Healthcheck `pg_isready`. Roles and extensions per §4.1. `max_connections` set explicitly from the connection budget (§9). |
| `migrator` | `Zaagl.Api` image, entrypoint `migrate` (EF Core migration bundles for `AppDbContext` and `AllocDbContext`, §4.1) | none | One-shot job, connects as the owner roles with `lock_timeout = 5s` and retries, runs both bundles, exits 0. API and coordinator `depends_on: migrator: condition: service_completed_successfully`. |
| `coordinator` | `Zaagl.Coordinator` | `backend` network only | Leases number ranges. Volume `coordstate` holds the HWM file (§6.4); writes length markers to the off-host allocator sink (§6.4). Exactly **one replica**. |
| `api` | `Zaagl.Api` | `edge` network, port 8080, **not published on the host** | Serves `zaa.gl` and `api.zaa.gl` (§1.2). Stateless except in-memory leases, cache and click buffer. Must run correctly as N replicas later. |
| `ops` | `Zaagl.Api` image, entrypoint `zaagl-ops` | none | One-off operator commands (`init`, `declare-restore`, admin calls). Compose profile `ops`, never started by default. |
| `mailpit` | `axllent/mailpit` | dev only (override file) | Catches outgoing email in development and tests. |

Rules:
- `restart: unless-stopped` on long-running services; healthchecks on all of them (§6.1, §7.7). The `api` Docker healthcheck uses `/health/live` (a database blip must not take the only replica out of the proxy); deploy gating uses `/health/ready`.
- **Networks**: `edge` (reverse proxy, `api`, the web app) and `backend` with `internal: true` (`api`, `coordinator`, `postgres`, `migrator`, `ops`). `api` reaches the internet (Web Risk, Turnstile, SMTP) through `edge`; `coordinator` and `ops` also join a non-internal `egress` network with no published ports, used only to reach the allocator sink. The web app never joins `backend`. The reverse proxy and the web app get **fixed addresses** on `edge` (`ipv4_address`), used as `KnownProxies` (§7.13).
- **Resource limits**: `mem_limit` on `api` and `coordinator` plus `DOTNET_GCHeapHardLimit`; Docker `json-file` logging with `max-size`/`max-file` on every service; `oom_score_adj` lowered for `postgres`. `pgdata` sits on its own volume.
- **Startup ordering after a reboot**: `api` and `coordinator` retry the database connection with backoff and never exit on a missing database; the coordinator runs its startup check (§6.4) only once the database is reachable.
- Secrets via Docker secrets (files), never in images, committed files, or the database. Required secrets: `PG_SUPERUSER_PASSWORD`, `APP_OWNER_PASSWORD`, `ALLOC_OWNER_PASSWORD`, `API_USER_PASSWORD`, `COORDINATOR_USER_PASSWORD`, `SCRAMBLE_KEY_V1` (32 random bytes, base64), `COORDINATOR_TOKEN`, `COORDINATOR_ADMIN_TOKEN`, `JWT_SIGNING_KEYS` (a JSON set of `{kid, key}` entries, ≥ 32 random bytes each; the newest signs, all validate, §7.4), `GUEST_KEY` (32 random bytes, for guest creator keys, §7.12), `DATA_PROTECTION_CERT` (PFX + password, encrypts the Data Protection key ring, §4.4), `ALLOC_SINK_CREDENTIALS` (off-host allocator sink, §6.4), `WEB_RISK_API_KEY`, `TURNSTILE_SECRET`, `ADMIN_BOOTSTRAP_PASSWORD`, `SMTP_PASSWORD`, `EMAIL_WEBHOOK_SECRET` (bounce and complaint webhooks, §7.10).
- **Credential isolation**: `api` receives `SCRAMBLE_KEY_V1`, `COORDINATOR_TOKEN` (leases, status, fingerprint), `JWT_SIGNING_KEYS`, `GUEST_KEY`, `DATA_PROTECTION_CERT`, `WEB_RISK_API_KEY`, `TURNSTILE_SECRET`, `EMAIL_WEBHOOK_SECRET`, `ADMIN_BOOTSTRAP_PASSWORD` (read once at first start to create the admin) and its own database and SMTP credentials. `COORDINATOR_ADMIN_TOKEN` is **never** mounted into `api`; only `ops` gets it. `ALLOC_SINK_CREDENTIALS` go only to `coordinator` and `ops`. The coordinator never receives the scramble key. `ops` also mounts `SCRAMBLE_KEY_V1` (to compute the key fingerprint for `init` and recovery, §5.4) and the API's database credentials (for `declare-restore`, account clean-up). The web app receives **none** of these secrets (§7.13).
- **Key escrow** (MUST): before `ops init`, `SCRAMBLE_KEY_V1` is stored in two independent places off the host (a secrets manager and a sealed offline copy), separate from database backups (§5.3).
- Containers run as non-root, read-only root filesystem where possible.
- **Staging** uses its own value for every secret above (never production's), sits behind an access gate that does not use the `Authorization` header, sends `X-Robots-Tag: noindex` on every host, and uses Mailpit instead of the SMTP provider.
- **Image tags**: the coordinator image is tagged by a hash of its inputs (`src/Zaagl.Codes`, `src/Zaagl.Contracts`, `src/Zaagl.Coordinator`, alloc migrations), so a deploy restarts it only when it changed. Every coordinator restart skips up to one reservation of numbers (§6.4).
- Provide `deploy/docker-compose.yml`, `deploy/docker-compose.override.yml` (dev, includes `mailpit`), `deploy/.env.example`, `deploy/secrets/README.md` with the command to generate each secret, and `deploy/init-db/` scripts (run as the superuser on first PostgreSQL start) that create the roles, the database, and `CREATE EXTENSION IF NOT EXISTS btree_gist` in it.
- **Migrations never run inside the API or coordinator.** Only `migrator` applies them. Never use `EnsureCreated`.
- **Migrations are expand-only**: every migration keeps the previous release working (add first; remove one release later), because a rollback redeploys the old image without undoing migrations. Indexes on `app.links` are created `CONCURRENTLY` in non-transactional migrations; indexes on `click_events` are created `ON ONLY` the parent, then built concurrently per partition and attached. A CI job runs the previous release's integration tests against the new schema (§12).
- **WAL archiving is mandatory in staging and production** (configured by the infra repository): `archive_mode = on`, `archive_timeout = 60s`, archive to off-host EU object storage. The coordinator depends on it for archive-confirmed reservations (§6.4) and reads `pg_stat_archiver`.

---

## 4. Data model

### 4.1 Schemas, roles, contexts
One PostgreSQL database `zaagl`, three schemas. Extension `btree_gist` is created by `init-db` (it needs superuser); migrations assume it exists and fail clearly if not.

| Schema | Owner role (used by `migrator` only) | Runtime role | Runtime rights |
|---|---|---|---|
| `app` | `app_owner` | `api_user` | SELECT/INSERT/UPDATE/DELETE on tables except `app.settings` (SELECT only), USAGE on sequences, EXECUTE on the two partition functions (§4.4) |
| `identity` | `app_owner` | `api_user` | SELECT/INSERT/UPDATE/DELETE on tables, USAGE on sequences |
| `alloc` | `alloc_owner` | `coordinator_user` | SELECT/INSERT/UPDATE on tables, USAGE on sequences |

- `api_user` has **no** rights on `alloc`. `coordinator_user` has **no** rights on `app` or `identity`.
- Runtime roles never own objects and cannot run DDL. `ALTER DEFAULT PRIVILEGES` in the migrations so new tables get the grants automatically. `REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA app FROM PUBLIC` **and** `ALTER DEFAULT PRIVILEGES FOR ROLE app_owner IN SCHEMA app REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC`, so functions added later are not public either. Functions and their grants live in idempotent `CREATE OR REPLACE` scripts re-applied by every migration that touches them.
- Role settings: `ALTER ROLE api_user SET idle_in_transaction_session_timeout = '60s'` (same for `coordinator_user`). `init-db` grants `pg_monitor` to `coordinator_user` (archive status reads, §6.4).
- Tests use the real roles: every Testcontainers fixture runs the `init-db` scripts and both migration bundles as the owner roles, then connects as `api_user` / `coordinator_user`. Negative grant tests prove `api_user` cannot read `alloc.*` or write `app.settings`, and `coordinator_user` cannot touch `app` or `identity`.
- Two DbContexts on the same database: `AppDbContext` (schemas `app`, `identity`) in the API, `AllocDbContext` (schema `alloc`) in the coordinator. Each has its own migrations-history table inside its own schema.

### 4.2 `alloc` schema
```
alloc.pools
  pool_id         text primary key                 -- 'default' in year 1
  current_length  smallint not null                -- 5 at start
  next_offset     bigint not null                  -- offset from range_start of current_length; numbers below are leased
  restore_detected_at timestamptz null             -- set by the startup check (§6.4)
  updated_at      timestamptz not null

alloc.pool_ranges                                  -- the slice of each length's number space owned by a pool
  pool_id         text not null references alloc.pools
  code_length     smallint not null check (code_length between 5 and 10)
  range_start     bigint not null                  -- inclusive
  range_end       bigint not null                  -- exclusive; MUST be <= GENERATOR_CAP[code_length] (§4.5)
  key_version     smallint not null default 1 check (key_version = 1)   -- fixed in Phase 0 (§5.3)
  block_size      int not null check (block_size between 100 and 1000000)
  reserve_ahead_blocks int not null default 10 check (reserve_ahead_blocks between 1 and 100)
  primary key (pool_id, code_length)
  check (range_start >= 0 and range_end > range_start)
  exclude using gist (code_length with =, int8range(range_start, range_end) with &&)

alloc.leases                                       -- append-only record of every lease
  lease_id        bigint generated always as identity primary key
  request_id      uuid not null unique
  pool_id         text not null
  node_id         text not null
  code_length     smallint not null
  key_version     smallint not null
  start_number    bigint not null
  end_number      bigint not null                  -- exclusive
  issued_at       timestamptz not null
  check (end_number > start_number)
  exclude using gist (code_length with =, int8range(start_number, end_number) with &&)

alloc.reservations                                 -- append-only; archive-confirmed reservations (§6.4)
  id bigint generated always as identity primary key, pool_id text not null, code_length smallint not null,
  reserved_offset bigint not null, created_at timestamptz not null, commit_lsn pg_lsn null, confirmed_at timestamptz null
  index (pool_id, code_length, reserved_offset)

alloc.installation                                 -- exactly one row once initialised (§6.4)
  installation_id uuid primary key
  key_fingerprint text not null                    -- §5.4; immutable
  initialized_at  timestamptz not null

alloc.recovery_log                                 -- audit of every init / recover / admin action
  id bigint generated always as identity primary key, at timestamptz, mode text, details jsonb, operator text
```
- The exclusion constraints make overlapping pool slices and overlapping leases **impossible at the database level**.
- **The database is authoritative for ranges.** Defaults below are used **only** by the seeding migration. After that, ranges change only through the coordinator admin endpoint (§6.1).

Seed (pool `default`, `current_length = 5`, `next_offset = 0`, `range_start = 0`, `range_end = floor(62^L × 0.01)`, `key_version = 1`), **all six lengths 5–10**:

| Length | 62^L | range_end (1%) | GENERATOR_CAP (2%) | Feistel bits | block_size |
|---|---|---|---|---|---|
| 5 | 916,132,832 | 9,161,328 | 18,322,656 | 30 | 1,000 |
| 6 | 56,800,235,584 | 568,002,355 | 1,136,004,711 | 36 | 10,000 |
| 7 | 3,521,614,606,208 | 35,216,146,062 | 70,432,292,124 | 42 | 10,000 |
| 8 | 218,340,105,584,896 | 2,183,401,055,848 | 4,366,802,111,697 | 48 | 100,000 |
| 9 | 13,537,086,546,263,552 | 135,370,865,462,635 | 270,741,730,925,271 | 54 | 100,000 |
| 10 | 839,299,365,868,340,224 | 8,392,993,658,683,402 | 16,785,987,317,366,804 | 60 | 100,000 |

Length 10 is the last length this build supports (62^11 overflows `long`). Document it.

### 4.3 `app.links` and link states
```
id              bigint generated always as identity primary key
code            varchar(64) collate "C" not null       -- case-sensitive, byte order
is_custom       boolean not null
code_length     smallint null                          -- generated codes only
code_number     bigint null                            -- generated codes only: number before scrambling
key_version     smallint null                          -- generated codes only
lease_id        bigint null                            -- generated codes only (no FK: other schema)
target_url      text not null
target_host     text not null                          -- normalised host of target_url (§7.6), for takedowns and blocklists
owner_id        uuid null references app.user_profiles -- null for guest links
guest_creator_key bytea null                           -- guest links only: HMAC of the creator's IP partition (§7.12)
created_at      timestamptz not null
expires_at      timestamptz not null                   -- every link expires (§1.1)
purge_after     timestamptz not null                   -- = expires_at + quarantine; recomputed on renewal
deleted_at      timestamptz null
disabled_at     timestamptz null
disabled_kind   text null check (disabled_kind in ('admin','user-disabled','abuse','safety'))
disabled_reason text null                              -- free text shown to admins
click_count     bigint not null default 0              -- non-bot clicks; also kept for guest links (aggregate only, §7.3)
last_clicked_at timestamptz null
safety_checked_at timestamptz null                     -- last destination reputation check (§7.15)
expiring_notice_queued_at timestamptz null             -- §7.10; reset on renewal
expired_notice_queued_at  timestamptz null
purge_notice_queued_at    timestamptz null

constraint uq_links_code unique (code)
create unique index uq_links_number on app.links (code_length, code_number) where code_number is not null;   -- PARTIAL unique index
check ( (is_custom and code_length is null and code_number is null and key_version is null and lease_id is null)
     or (not is_custom and code_length is not null and code_number is not null and key_version is not null and lease_id is not null
         and char_length(code) = code_length) )
check ( (disabled_at is null) = (disabled_kind is null) )
check ( purge_after >= expires_at )                    -- equal for guest links (no quarantine)
check ( (owner_id is null) = (guest_creator_key is not null) )
index (guest_creator_key, created_at) where guest_creator_key is not null
index (owner_id, created_at desc, id desc)
index (owner_id, is_custom, created_at)                -- alias limits (§7.4)
index (expires_at), index (purge_after)
index (target_host, created_at)                        -- takedowns and re-checks (§7.15)
with (fillfactor = 80, autovacuum_vacuum_scale_factor = 0.02, autovacuum_analyze_scale_factor = 0.02)
```
- In EF Core: `HasAlternateKey(l => l.Code).HasName("uq_links_code")` with `.UseCollation("C")` on `Code`, so `ON CONFLICT ON CONSTRAINT uq_links_code` (§7.2) finds a **constraint**, not just an index; `HasIndex(l => new { l.CodeLength, l.CodeNumber }).IsUnique().HasFilter("code_number IS NOT NULL").HasDatabaseName("uq_links_number")`. The fillfactor and autovacuum settings are set in the first migration (a later change needs a table rewrite). `click_count` and `last_clicked_at` are never indexed, so counter updates stay HOT updates; monitor `n_tup_hot_upd / n_tup_upd` (target > 0.9).
- `purge_after = expires_at + Links:QuarantineDays` for account-owned links; `purge_after = expires_at` for guest links, so they go straight from `active` to `purgeable` (`404`). Deleting a link does **not** change `purge_after`: a deleted alias stays unavailable to others until then.
- **The unique constraint on `code` is the only authority for alias ownership.** An alias is free when no row has that code, or the row is `purgeable`, **and** it is not in `app.blocked_aliases` (§7.15) and not matched by the alias denylist (§4.5).

**Link state** — computed by one function `LinkState Compute(Link l, DateTimeOffset now)`, evaluated **top to bottom, first match wins**. It is total.

| # | State | Condition | Redirect | Visible to owner | Renewable |
|---|---|---|---|---|---|
| 1 | `purgeable` | `now ≥ purge_after` (row not yet purged) | `404` | no | no |
| 2 | `deleted` | `deleted_at` set | `410` | no (admin only) | no |
| 3 | `disabled` | `disabled_at` set | `410` | yes | no |
| 4 | `quarantined` | `now ≥ expires_at` | `410` | yes | yes |
| 5 | `active` | otherwise | redirect | yes | yes |

Invariant (tested): **no row whose `expires_at` has passed ever redirects**, whether or not the purge job has run.

### 4.4 Other tables and functions
- `app.user_profiles(user_id uuid primary key, plan text not null check (plan in ('free','premium')) default 'free', plan_changed_at timestamptz not null, disabled_at timestamptz null, disabled_reason text null, disabled_with_links boolean null, tos_version text not null, tos_accepted_at timestamptz not null, deleted_at timestamptz null)`. No foreign key to the Identity user: after account deletion (§7.14) the Identity row is gone and this row remains as a **tombstone** (`deleted_at` set, no personal data) until the user's last link is purged; links reference `user_profiles.user_id`.
- Email confirmation uses Identity's `EmailConfirmed` column; no extra table.
- `app.data_protection_keys` — the ASP.NET Core Data Protection key ring (`PersistKeysToDbContext<AppDbContext>`), **encrypted at rest** with `ProtectKeysWithCertificate` using `DATA_PROTECTION_CERT`, so a database or backup leak cannot forge reset or confirmation tokens.
- `app.alias_freezes(id bigint identity primary key, starts_at timestamptz not null, until timestamptz not null, source text check in ('coordinator','operator'), source_ref text null unique, reason text, created_at)` (§7.11).
- `app.settings(key text primary key, value text not null)`, owned by `app_owner`, SELECT-only for `api_user`. Seeded with `click_retention_months = 6` (allowed 3..36) and `test_clock_allowed = false`.
- `app.link_archive(link_id bigint primary key, owner_id uuid not null, code varchar(64) not null, is_custom boolean not null, target_url text not null, created_at, expires_at, purged_at timestamptz not null)`, index `(owner_id, purged_at desc)`. Purged links of **premium** owners, so their statistics stay queryable until the owner deletes the account (§7.14). `code` here is history only; it blocks nothing.
- `api_keys(id, user_id, name varchar(100), prefix char(8) unique, secret_hash bytea, scopes text[], created_at, last_used_at, revoked_at)`. Format `zgl_<prefix8>_<secret32 base62>`, `RandomNumberGenerator`, SHA-256 stored, plaintext shown once, max 20 active per user.
- `refresh_tokens(id, user_id, family_id uuid, token_hash bytea unique, created_at, expires_at, used_at, revoked_at)` (§7.4).
- `idempotency_keys(scope varchar(80), key varchar(128), request_hash bytea, status text check in ('pending','done'), status_code int, response_body jsonb, created_at, primary key(scope, key))`. `scope` = `u:<userId>` for accounts, `g:<hex guest creator key>` for guests. Cleanup after 24 h.
- `email_outbox(id bigint identity primary key, user_id uuid, kind text check in ('confirm-email','password-reset','account-exists','security-notice','expiring','expired','purge'), digest_date date null, payload jsonb null, created_at, send_after timestamptz, status text check in ('pending','sending','sent','cancelled','failed') default 'pending', locked_until timestamptz null, sent_at timestamptz null, attempts int default 0, last_error text null)`, partial unique index `(user_id, kind, digest_date) where kind in ('expiring','expired','purge')`, index `(status, send_after)`. `payload` **never contains a token**: confirmation and reset tokens are generated at send time (§7.10).
- `app.email_suppressions(address_hash bytea primary key, reason text check in ('bounce','complaint','manual'), created_at)`: SHA-256 of the lower-cased address; the dispatcher never sends to a suppressed address (§7.10).
- `email_outbox_items(outbox_id bigint references app.email_outbox on delete cascade, link_id bigint, expires_at_snapshot timestamptz not null, primary key (outbox_id, link_id))`, index `(link_id)`.
- `click_events`: range-partitioned by month on `occurred_at` (UTC). Columns `link_id bigint, occurred_at timestamptz, referrer_host text null, ua_family text null, country text null`. **No IP data of any kind** and **no bot rows** (bots are only counted, below). `country` is filled only when an optional GeoIP database is configured (`Analytics:GeoIpDbPath`), derived before enqueueing; the address is then discarded. Index `(link_id, occurred_at)`. Created with raw SQL in a migration with a **DEFAULT partition** `click_events_default` and partitions for the current month and the next 3 months. Mapped in EF Core with `ExcludeFromMigrations()`. Raw events follow `click_retention_months` for every plan; at most `Analytics:RawEventsPerLinkPerHour` (default 1,000) raw rows per link per hour, beyond which only the counters move (§7.5).
- `link_daily_stats(link_id, day date, clicks bigint, bot_clicks bigint not null default 0, primary key(link_id, day)) with (fillfactor = 90)`. `day` is the **UTC** date. Rows of purged links: deleted in the purge transaction for free owners; kept for premium owners (§7.8) until account deletion.
- Abuse tables (§7.15): `app.blocked_destinations(id bigint identity primary key, kind text check in ('host','host-suffix','url-prefix'), pattern text not null, reason text not null, created_by uuid null, created_at, unique (kind, pattern))`; `app.blocked_aliases(code varchar(64) collate "C" primary key, reason text not null, link_id bigint null, blocked_at timestamptz not null)`; `app.alias_denylist(id bigint identity primary key, pattern text not null unique, kind text check in ('exact','contains'), created_at)` (seeded, §4.5); `app.abuse_reports(id bigint identity primary key, code varchar(64) null, url text null, category text not null, details text null, reporter_email text null, created_at, status text check in ('open','actioned','dismissed') default 'open', resolved_by uuid null, resolved_at timestamptz null, resolution text null)`; `app.abuse_evidence(link_id bigint primary key, code varchar(64) not null, target_url text not null, owner_id uuid null, guest_creator_key bytea null, created_at, disabled_at, disabled_kind text not null, reason text, keep_until timestamptz not null)` (kept `Abuse:EvidenceRetentionDays`, default 365); `app.takedowns(id bigint identity primary key, actor uuid not null, filter jsonb not null, links_affected int not null, reason text not null, created_at)`; `app.guest_quota(prefix_key bytea, hour timestamptz, count int not null, primary key (prefix_key, hour))` (§7.12); `app.safety_budget(month date primary key, used int not null)` (§7.15).

**Partition functions** — both `SECURITY DEFINER`, owned by `app_owner`, `SET search_path = pg_catalog, app, pg_temp`, `SET lock_timeout = '2s'` (a lock timeout raises; the job retries hourly and alerts after a day of failures; partitions exist 3 months ahead, so retries are harmless), `EXECUTE` revoked from `PUBLIC` and granted only to `api_user`. Partition names are built only from validated dates (`click_events_yYYYYmMM`). Both take `p_today date` from the application's `TimeProvider`; if `abs(p_today − current_date) > 2` they raise an error unless `app.settings.test_clock_allowed = true` (set only in test databases).

1. `app.ensure_click_partitions(p_today date, months_ahead int)`: rejects `months_ahead` outside 1..12. For each missing month from the month of `p_today` to `months_ahead` ahead, in one transaction: `LOCK TABLE app.click_events_default IN ACCESS EXCLUSIVE MODE`; create the month table `(LIKE app.click_events INCLUDING DEFAULTS INCLUDING CONSTRAINTS)`; move that month's rows from DEFAULT into it; `ALTER TABLE app.click_events ATTACH PARTITION …`.
2. `app.drop_expired_click_partitions(p_today date)`: reads `click_retention_months` and drops monthly partitions whose whole month ended more than that many months before `p_today`; deletes DEFAULT rows older than the same cutoff. Retention is **month-aligned**.

### 4.5 Code namespace: reserved words, aliases and the generator cap
Generated codes and custom aliases share one namespace (`zaa.gl/<code>`). These rules make them **provably disjoint** without lookup, history or coordination.

**Definitions**
- **Generated shape**: a string of length L ∈ 5..10 consisting only of Base62 characters.
- **GENERATOR_CAP[L]** = `floor(62^L × 0.02)` (table in §4.2). A compile-time constant in `Zaagl.Codes`, covered by the committed test vectors. **It must never increase.** Every pool range satisfies `range_end ≤ GENERATOR_CAP[L]`.
- **Generator-reachable**: a string `s` of generated shape with `Unpermute(L, 1, Base62.Decode(s)) < GENERATOR_CAP[L]`. Exactly these strings can ever be produced by the generator.

**Rules**
1. A custom alias that is **generator-reachable is rejected** (`409 alias-unavailable`). About 2% of 5–10-character alphanumeric aliases are affected; shorter, longer, or containing `-`/`_` never.
2. Hence the generator can never produce an accepted alias, and no alias can ever be a past or future generated code.
3. **Reserved words** (compared case-insensitively against the first path segment). The list is **final before the first production alias exists**: adding a word later would break a live alias or code. `api`, `app`, `www`, `health`, `status`, `swagger`, `openapi`, `docs`, `admin`, `static`, `assets`, `favicon.ico`, `robots.txt`, `sitemap.xml`, `ads.txt`, `.well-known`, `security.txt`, `account`, `login`, `signin`, `signup`, `register`, `logout`, `verify`, `password`, `confirm-email`, `reset-password`, `support`, `help`, `contact`, `abuse`, `report`, `preview`, `terms`, `privacy`, `legal`, `dmca`, `about`, `pricing`, `blog`, `zaagl`, `files`, `file`, `snippets`, `snippet`, `images`, `raw`, `embed`, `download`, `upload`. Aliases matching them are rejected; generated codes matching them are **skipped** (§7.2). Unit test over every case variant.
4. **Alias syntax**: 4–64 characters from `[A-Za-z0-9_-]`, case-sensitive, not reserved, not generator-reachable, not denylisted, not in `app.blocked_aliases`.
5. **Alias denylist** (`app.alias_denylist`, seeded, admins can add): exact words and `contains` fragments, compared after lower-casing and normalising look-alike digits (`0→o`, `1→l`, `3→e`, `4→a`, `5→s`, `7→t`) and removing `-`/`_`. Seed: strong phishing terms as `contains` (`login`, `signin`, `logon`, `verify`, `password`, `passwd`, `wallet`, `banking`); common words as `exact` only, so ordinary aliases such as `support-docs` or `update2026` stay allowed (`secure`, `account`, `invoice`, `payment`, `support`, `update`, `recover`, `unlock`); widely phished brand names as `exact` and `contains` (maintained list in `docs/DECISIONS.md`). Admins can remove entries; every change is audited. A match → `409 alias-unavailable` (same response as any unavailable alias). Admins can also add a single alias to `app.blocked_aliases` (brand complaints).
6. **Future products**: a single path segment of **1–3 characters** can never be an alias (minimum 4) or a generated code (minimum 5), and multi-segment paths are never codes. These are reserved for files, images and snippets (for example `zaa.gl/f/<id>`, `zaa.gl/s/<id>`, `zaa.gl/i/<id>`); the redirect returns `404` for them in this build. Content served there later **must** be served so it cannot run in the `zaa.gl` origin (attachment downloads or a sandboxed CSP) and `zaa.gl` never sets cookies (§1.2).

Unit tests: (a) for each length, 1,000,000 generated codes are all generator-reachable; (b) aliases from `Encode(Permute(L, 1, n), L)` with random `n < GENERATOR_CAP[L]` are all rejected, with `n ≥ GENERATOR_CAP[L]` all accepted unless denylisted; (c) aliases with `-` or `_`, or of length 4 or 11+, never call `Unpermute`.

### 4.6 Room for later expansion
- A future second region gets its own pool with its own slice of `[0, GENERATOR_CAP[L])`, leased by a regional coordinator that writes to the primary's `alloc` schema; the exclusion constraint prevents overlaps. Writes always go to the one primary (§9).
- `key_version` columns exist for a future key rotation (§5.3).
- API instances identify themselves with `NODE_ID` (default: container hostname).

---

## 5. The code generator, precise spec

Shared library `Zaagl.Codes`, no dependencies beyond the BCL: `Base62`, the permutation, `GENERATOR_CAP`, `IsReserved`, `IsGeneratorReachable`, `KeyFingerprint`.

```csharp
public interface ICodePermutation   // keyed bijection on [0, 62^L)
{
    long Permute(int length, short keyVersion, long number);
    long Unpermute(int length, short keyVersion, long value);
}
public static class Base62          // alphabet "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
{
    public static string Encode(long value, int length);   // left-pad with '0' to exactly `length`; throw if it does not fit
    public static bool TryDecode(ReadOnlySpan<char> code, out long value);
}
```

### 5.1 Permutation
- Domain for length L: `N_L = 62^L`. Bit width `b` = the smallest **even** b with `2^b ≥ N_L` (30, 36, 42, 48, 54, 60 for L = 5..10). `h = b/2`, `mask = 2^h − 1`.
- **Balanced Feistel network, 10 rounds**, round index i = 0..9:
  - `Lh = x >> h`, `Rh = x & mask`.
  - Forward round: `F = PRF(i, Rh) & mask`; `(Lh, Rh) = (Rh, Lh ^ F)`. Output `(Lh << h) | Rh`.
  - Inverse round, i = 9..0: `F = PRF(i, Lh) & mask`; `(Lh, Rh) = (Rh ^ F, Lh)`.
- **PRF(i, r)** = first 8 bytes, read as big-endian `ulong`, of `HMAC-SHA256(subkey, message)`, `message` = 9 bytes `[ (byte)i ][ r as 8-byte big-endian unsigned ]`. `HMACSHA256.HashData` with stack-allocated spans.
- **Subkey** for (L, keyVersion): `HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm: SCRAMBLE_KEY_V{keyVersion} bytes, outputLength: 32, salt: UTF8("zaagl/codes"), info: UTF8($"perm/len{L}"))`, `L` in the invariant culture. Derived once at startup, cached.
- **Cycle walking**: `y = Feistel(x); while (y >= N_L) y = Feistel(y);`, the same with the inverse in `Unpermute`. Expected iterations 1.17–1.37. Throw (and log critical) after 10,000.
- Code for leased number `n`: `Base62.Encode(Permute(L, 1, n), L)`.

### 5.2 Tests
- Base62: round-trip, padding, overflow, invalid characters, case-sensitivity.
- Exhaustive bijection test on a small domain (generic over `N`, test `N = 62^3`).
- 1,000,000 random round-trips per length 5..10.
- **Committed test vectors** (`tests/Zaagl.Codes.Tests/vectors.json`): key = bytes `0x00..0x1F`; for each L in 5..10 and n in {0, 1, 2, 1000, GENERATOR_CAP[L] − 1} the expected code; the `GENERATOR_CAP` values; the key fingerprint of that key.
- The namespace tests of §4.5.
- BenchmarkDotNet: `Permute` + `Encode` p99 under 20 µs; `IsGeneratorReachable` p99 under 20 µs.

### 5.3 Key policy (Phase 0)
- One key, `SCRAMBLE_KEY_V1`, for every length, **forever in this build**. `key_version` is always 1 (CHECK constraint); the columns exist for later.
- Key rotation is **not supported** in this build: the alias rule of §4.5 depends on the key of each length. Document the compatible path for later in `docs/DECISIONS.md`: a new key may be bound only to a length **that has not started yet** (`pool_ranges.key_version`), so every code and alias check of a started length keeps its key.
- **The key is permanent data, not a rotating secret.** Losing it disables all creation for good (§5.4). Escrow is mandatory (§3): two independent off-host copies made before `ops init`; `ops init` refuses to run without `--key-escrowed`; deploy scripts fail if the key is missing and never generate one; the quarterly restore drill checks the escrowed copy's fingerprint against `alloc.installation`.
- **Codes are not secrets.** If the key leaks, every generated code becomes enumerable; even without a leak, random guessing finds live 5-character codes at roughly 1 in 100 attempts once length 5 is 1% full. Mitigations: tiered miss limits and an enumeration alert (§7.3), short free-plan expiry. Never use a short code as an access token; future private content uses separate long random tokens. Accepted risk in `docs/DECISIONS.md`.
- The API refuses to start without `SCRAMBLE_KEY_V1`. A lease with `keyVersion ≠ 1` is a fatal configuration error (§7.1).

### 5.4 Key fingerprint (protection against deploying a different key)
Every guarantee of §4.5 and every generated code depends on all API instances using **the same** key for the life of the installation.
- `keyFingerprint` = lowercase hex of `SHA-256(UTF8("zaagl/key-fingerprint/v1") ‖ key bytes)`. Not secret.
- **Recorded once at initialisation** by `ops init`, which computes it from the mounted key (§6.4). Stored in the HWM file and in `alloc.installation.key_fingerprint`; both must agree. It never changes; recovery preserves it (§6.4).
- **Checked by the coordinator** on every `POST /v1/leases` (the request carries `keyFingerprint`); mismatch → `409 key-mismatch`, critical log.
- **API behaviour**: at startup the API computes its fingerprint and calls `GET /v1/status`. Until a successful match, and after any mismatch, the API is in **creation-faulted** state: generated creation and **alias creation** return `503`; the issuer stops leasing; `/health/issuer` shows `KeyMismatch` or `KeyUnverified`. Redirects, reads, renewals, deletes and everything else keep working. While the coordinator is unreachable at startup, the API retries the check with backoff; creation stays disabled until it succeeds.
- `IsGeneratorReachable` is only evaluated after a successful fingerprint check in this process.
- Test: a second API instance with a different key never issues a code or accepts an alias; the first instance is unaffected.

---

## 6. Coordinator service (`Zaagl.Coordinator`)

The coordinator does **one** job: leasing number ranges. It is never on the redirect path and never involved in aliases.

### 6.1 API (internal network only)
**Runtime** (bearer `COORDINATOR_TOKEN`)
- `GET /v1/status` → `200 { keyFingerprint, leasing: "ok"|"blocked"|"not-initialised", leasingReason, restoreDetectedAt }`; `leasingReason` includes `history-found`, `wal-archiving-off`, `reservation-pending`, `sink-unreachable`, `lengths-exhausted`.
- `POST /v1/leases` body `{ requestId: uuid, nodeId: string(1..64), poolId: string, keyFingerprint }` → `200 { leaseId, poolId, codeLength, keyVersion, startNumber, endNumber }`. Reused `requestId` with different `poolId`/`nodeId` → `409`; `409 key-mismatch`; `503` when leasing is blocked or not initialised; `507` when length 10 is exhausted.
- `GET /v1/pools/{poolId}`: current length, next offset, range end, fill ratio, HWM state, latest reservation and its confirmation age, lengths remaining, `restore_detected_at`, leases in the last hour.

**Admin** (bearer `COORDINATOR_ADMIN_TOKEN`, used only from `ops`). All run under the coordinator semaphore and the HWM-before-commit rule, all **forward-only**, each writes a row to `alloc.recovery_log` with the `operator` field:
- `POST /v1/admin/init` body `{ operator, keyFingerprint }` (§6.4).
- `POST /v1/admin/recover` body `{ operator, mode, … }` (§6.4).
- `POST /v1/admin/pools/{poolId}/advance` body `{ operator, margin }` (1 ≤ margin ≤ 1,000 × block size; rolls to the next length past `range_end`) or `{ operator, toLength }` (> `current_length`). Whenever it changes `current_length`, it first writes the length marker for the new length (write-once; an identical existing marker counts as success); sink failure → `503`, nothing changed.
- `PUT /v1/admin/pools/{poolId}/ranges/{length}` body `{ operator, rangeEnd?, blockSize?, reserveAheadBlocks? }`: `rangeEnd ≤ GENERATOR_CAP[L]`; if the length has started, `rangeEnd ≥ max(highest lease end_number, range_start + next_offset, range_start + HWM reservedOffset)`; `blockSize` 100..1,000,000; `reserveAheadBlocks` 1..100; checked arithmetic.

**Health**
- `GET /health/live`: process up. `GET /health/ready`: process up and database reachable (Docker uses this; a blocked allocator does not make it unready, so status and diagnostics stay reachable).
- Leasing state is reported by `GET /v1/status`; monitoring alerts on anything but `ok`.

### 6.2 Lease algorithm
All lease and admin operations run **one at a time** (a process-wide `SemaphoreSlim(1,1)`) and also take the PostgreSQL row lock.

1. Acquire the semaphore.
2. `BEGIN` (READ COMMITTED).
3. `SELECT … FROM alloc.pools WHERE pool_id = @p FOR UPDATE`.
4. `SELECT … FROM alloc.leases WHERE request_id = @r`. Found: different `poolId`/`nodeId` → `409`; else commit and return it.
5. Load the `pool_ranges` row for `current_length`. `start = range_start + next_offset`; `end = min(start + block_size, range_end)` (checked).
6. If `start >= range_end`: move to the next length. **Before** any lease of the new length is committed, write its **length marker** to the allocator sink (below) and wait for success; failure → rollback, leasing `blocked`, `503`. Then `current_length += 1`, `next_offset = 0`, back to step 5. No next length → rollback, `507`, critical log.
7. **HWM before commit** (§6.4): if `(current_length, end − range_start)` is beyond the HWM reservation, write a new reservation `(current_length, min(end − range_start + block_size × reserve_ahead_blocks, range_end − range_start))` durably. Write failure → rollback, release the semaphore, leasing `blocked`, `503`. **No lease exists in the database unless the HWM already covers it.**
7a. **Archive-confirmed reservation** (§6.4): `(current_length, end − range_start)` must also be covered by a reservation in `alloc.reservations` whose WAL is confirmed archived. If it is not (the background renewal fell behind), roll back and answer `503 reservation-pending` with `Retry-After: 5`; the API keeps serving from its buffered leases. **No lease exists in the database unless an archived reservation covers it.**
8. `UPDATE alloc.pools SET current_length, next_offset = end − range_start, updated_at = @now`.
9. `INSERT INTO alloc.leases …` (with `key_version` from `pool_ranges`). A unique violation on `request_id` → rollback, re-run from step 2 once.
10. `COMMIT`, release the semaphore, respond.

Leases never span two lengths. Leased numbers are **never** returned or reissued.

### 6.3 Concurrency test
200 parallel lease requests from 20 simulated nodes, including 20 duplicated `requestId`s sent concurrently: no overlaps, duplicates return identical leases, ranges contiguous, HWM ≥ every committed lease.

### 6.4 Initialisation, HWM and recovery
**HWM file** `/state/hwm.json` on `coordstate`:
```json
{ "format": 1, "installationId": "…", "keyFingerprint": "…", "state": "initializing" | "active",
  "pools": { "default": { "length": 5, "reservedOffset": 13000, "writtenAt": "…", "sha256": "…" } } }
```
`(length, reservedOffset)`: every number of a smaller length may have been issued, and for this length every offset below `reservedOffset` may have been issued. Written only inside the serialized section: temp file, `fsync`, atomic rename, `fsync` directory. The new value must be **≥** the current one.

**Archive-confirmed reservations** (MUST; product decision v9.4: the reservation lives in the database, and recovery after losing the host resumes where it stopped). The local HWM file protects against a database restored to an *older* point while the host survives; it is lost with the host. To survive host loss, the coordinator also keeps its reservation **in PostgreSQL** and only issues numbers that a restore to the latest archived WAL is guaranteed to bring back:
- `alloc.reservations(id bigint identity primary key, pool_id text not null, code_length smallint not null, reserved_offset bigint not null, created_at timestamptz not null, commit_lsn pg_lsn null, confirmed_at timestamptz null)`, append-only.
- **Renewal** (background, under the semaphore). Target `(L, p)` = `(current_length, next_offset)`, or `(current_length + 1, 0)` when `next_offset = range_end − range_start` and that length exists (its length marker is written first). If no confirmed **or pending** reservation of length `L` reaches `p + ½ × block_size × reserve_ahead_blocks`, insert `(L, min(p + block_size × reserve_ahead_blocks, range size of L))` in its own transaction, commit, then record `commit_lsn = pg_current_wal_insert_lsn()` (read after the commit, so it is at or after the commit record). At startup, committed rows whose `commit_lsn` is still null get `pg_current_wal_insert_lsn()` (conservative).
- **Every operation that sets the pool position** (startup row 3, `reattach`, `trust-database`, `advance`, `resume`, `new-length`, and the length change of §6.2 step 6) first inserts a reservation row at or beyond the new position; leasing stays `reservation-pending` until that row is confirmed.
- **Confirmation**: poll every 5 s while a row is unconfirmed. Use `pg_stat_archiver.last_archived_wal` only when it matches `^[0-9A-F]{24}$` (ignore `.history`, `.partial` and `.backup` names); parse timeline and segment number and compare **as numbers** with the row's segment (`pg_walfile_name(commit_lsn)`) on the current timeline. A row is confirmed only when that segment number is ≤ the last archived one **and** `pg_ls_archive_statusdir()` lists no `.ready` file for any segment ≤ the row's segment (so no earlier segment is still waiting); then set `confirmed_at`. `init-db` grants `pg_monitor` to `coordinator_user` for these reads. With `archive_timeout = 60s` (infra, §3) confirmation takes at most about a minute, far less than the headroom of half a reservation.
- Step 7a of §6.2 uses only **confirmed** rows. Therefore every number ever issued is below a reservation that is in the off-host WAL archive.
- If WAL archiving stops, confirmation stops: generated-code leasing blocks once the confirmed reservation is used up (`503 reservation-pending`, alert); aliases, redirects and everything else keep working. If `archive_mode` is off at startup, leasing is `blocked` with reason `wal-archiving-off` unless `Allocator:RequireArchiveConfirmation` is false (development and in-process tests only, with a fake archiver).

**Allocator sink** (off-host, MUST): an S3-compatible object store in the EU with versioning and object lock, credentials `ALLOC_SINK_CREDENTIALS` (coordinator and `ops` only). It holds two kinds of write-once objects (conditional put, `If-None-Match: *`), keyed by **pool, not by installation**, so they can be found even when every copy of the installation id is lost:
- the **installation manifest** `zaagl/manifest.json` `{ installationId, keyFingerprint, pools, initialLength, createdAt }`, written at initialisation;
- one **length marker** per started length, `zaagl/pools/<poolId>/length-started/<length>` `{ installationId, keyFingerprint, poolId, length, at }`, written synchronously before the first lease of that length commits (§6.2 step 6) and at initialisation.
Markers and the manifest are the fallback evidence when a restore cannot reach the latest WAL (`new-length`). The coordinator also emits the log events below for monitoring.

**Initialisation** (never automatic): `docker compose run --rm ops init --key-escrowed` computes the key fingerprint and calls `POST /v1/admin/init`. Preconditions: no `alloc.installation` row, no rows in `alloc.leases`, no HWM file, WAL archiving on (unless disabled for development), and the allocator sink reachable and **completely empty** (no manifest, no markers): a non-empty sink means codes may already have been issued, so `init` refuses and the operator must recover instead. Steps (crash-safe, resumable):
1. Write HWM with a new `installationId`, the `keyFingerprint`, `state: "initializing"`, positions = database positions. Write the manifest, then the length marker for the initial length (5), then the first reservation row (confirmed before any lease).
2. Insert the `alloc.installation` row with the same id and fingerprint; commit.
3. Rewrite HWM with `state: "active"`.

**Startup check** (under the semaphore). "Blocked" means leasing `blocked`; the process stays up and healthy otherwise.

| # | Observed state | Action |
|---|---|---|
| 1 | No HWM file, no installation row, no leases, **sink empty** | `not-initialised`; log "awaiting init". |
| 1a | No HWM file, no installation row, no leases, but the **sink has a manifest** | A database from before initialisation was restored after codes were issued. **Blocked** (`history-found`). Recovery: `new-length` (adopts the manifest's identity). |
| 2 | HWM `initializing`, installation row missing or with the same id and fingerprint, no leases | Interrupted init: finish step 1 (manifest, marker, first reservation and its confirmation; an identical existing object counts as success), then steps 2–3 automatically. |
| 1b | Sink unreachable while no installation row exists | **Blocked** (`sink-unreachable`); retried every minute. |
| 3 | HWM `active`, installation row with the same id and fingerprint, database **behind** HWM | Normal after every restart (gap ≤ one reservation) or a database restore. Set the database to the HWM. If the gap exceeds one reservation, set `restore_detected_at = @now`, log critical. |
| 4 | Same, database **equal** | OK. |
| 5 | Same, database **ahead** of HWM | Violates HWM-before-commit (HWM rolled back or lost writes). **Blocked.** Recovery: `trust-database` or `new-length`. |
| 6 | HWM `active`, installation row **missing** | Database restored to before init, or replaced. **Blocked.** Recovery: `reattach`. |
| 7 | HWM missing while the database is initialised; HWM unreadable or bad checksum; or HWM and installation row disagree on id or fingerprint | **Blocked.** Recovery: `reattach` (if the HWM is valid); after losing the host with PostgreSQL restored to the latest archived WAL, `resume`; otherwise `new-length` (after moving a bad file aside). |

**Recovery modes** (`POST /v1/admin/recover` via `ops`; each logged; each ends with a durable `active` HWM):
- `reattach` `{ }`: requires a valid `active` HWM. Recreates or corrects the installation row from the HWM's id and fingerprint and moves the allocator to the HWM. **Always safe.**
- `trust-database` `{ confirmDatabaseComplete: true }`: requires a valid HWM behind the database (row 5). The operator asserts the database was never restored since the HWM was last correct. Writes the HWM from the database position plus one reservation.
- `resume` `{ }`: the normal recovery after losing the host, when PostgreSQL was restored to the end of the WAL archive. **The operator's word is not enough; `ops recover resume` verifies the restore first**: it reads the new timeline's `.history` file from the WAL archive to get the parent timeline and its switch LSN, lists the archive, and refuses unless no segment of the parent timeline exists after the segment that contains the switch LSN (a restore that stopped early at a missing or corrupt segment fails this check). It also refuses unless the database has an installation row equal to the manifest identity, at least one confirmed reservation exists, the sink is reachable, and no marker names a length higher than the highest reservation's length. If any check fails or cannot run, only `new-length` is allowed. On success the coordinator moves to the maximum, in (length, offset) order, of: a valid HWM (if any), the pool position, the highest lease end and the highest reservation (confirmed or not); it writes a new reservation (and waits for its confirmation) and a new HWM. Because numbers are only ever issued under archived reservations, nothing issued before the loss can be issued again, and **no length is wasted** (at most one reservation is skipped). Startup row 1a allows only `new-length`.
- `new-length` `{ }`: the fallback when the restore could not reach the latest WAL (for example the archive itself was damaged) or the HWM is untrustworthy. The coordinator reads every marker of the pool from the sink and computes `toLength = max(highest length in alloc.leases, highest length in alloc.reservations, highest marker length) + 1`; it moves the pool to offset 0 of `toLength`, writes the marker for `toLength`, and writes a new HWM. Cost: the rest of the abandoned length (its whole generation range, up to 1% of that length's space) is never used, and only six lengths exist, so the runbook treats `new-length` as a last resort; the coordinator reports how many lengths remain (`GET /v1/pools/{id}`) and alerts when two or fewer remain. When no length remains, generated-code creation stays blocked (`507`); aliases keep working.

**Identity during recovery.** The HWM, `alloc.installation` and the sink manifest must end every recovery with the **same** `installationId` and `keyFingerprint`. Resolution: the values of a valid HWM; else those of `alloc.installation`; else those of the sink manifest. A new id is created **only** by `init` on an empty sink, never by recovery. Recovery refuses if any two surviving copies differ in `keyFingerprint`, or if the HWM's id differs from the manifest's id (wrong key, wrong bucket or mixed volumes: an operator must investigate). `reattach` may replace an installation row whose id differs only when the HWM id equals the manifest id.

The coordinator logs `alloc.length_started {pool, length, at}` and, on every HWM write, `alloc.hwm_written {pool, length, reservedOffset}`, for monitoring; recovery does not depend on logs.

Waste in normal operation: at most `reserve_ahead_blocks × block_size` numbers per coordinator restart (10,000 at length 5, about 0.1% of that range).

### 6.5 Failure behaviour
While the coordinator is down or leasing is blocked: generated-code creation continues until current and prefetched leases run out, then `503`. Everything else (redirects, aliases, renewals, accounts, analytics) is unaffected. A coordinator outage at API startup also delays the key-fingerprint check, so creation of both kinds waits until it succeeds (§5.4).

**Runbook rules**: the `coordstate` volume is **never** restored to an older state. Restoring PostgreSQL alone is safe for the allocator. After losing the host: follow §8.1 (Host lost): `recover resume` once the restore is verified; `recover new-length` only when verification fails.

---

## 7. API service (`Zaagl.Api`)

### 7.0 Database connections and creation load
- Two `NpgsqlDataSource` instances with separate pools: **`redirects`** (used only by `GET/HEAD /{code}` lookups; `Maximum Pool Size` 30 by default; `Command Timeout` 2 s and `Timeout` (acquire) 2 s, so redirects fail fast instead of queueing) and **`main`** (everything else; 50). A slow creation path can therefore never starve redirects. Both use `No Reset On Close=true` and `Max Auto Prepare=0` (PgBouncer-safe, §9).
- A third, dedicated connection (not pooled, not through PgBouncer) runs `LISTEN zaagl_link_cache` and `LISTEN zaagl_link_cache_ping` for cache invalidation and listener liveness (§7.3).
- **Lock timeout policy**: every interactive transaction (create, renew, PATCH, delete, admin actions, account deletion) starts with `SET LOCAL lock_timeout = '3s'` and never resets it. SQLSTATE `55P03` at any step → roll back, `503` with `Retry-After: 1` (`alias-busy` during alias claims, `busy` elsewhere).
- **Lock order** on `app.links`: any statement that locks or updates more than one link row first runs `SELECT id … ORDER BY id FOR NO KEY UPDATE` (or `FOR UPDATE`) on exactly those rows; bulk changes run in chunks of at most 1,000 rows per transaction; SQLSTATE `40P01` (deadlock) is retried once.
- A **creation concurrency limiter** (`ConcurrencyLimiter`, default 32 permits, queue 64) wraps `POST /api/v1/links`; excess requests get `503` with `Retry-After: 1`.
- No transaction is ever open while waiting for the coordinator (§7.2).

### 7.1 Code issuing (`ICodeIssuer`, singleton)
Lease object (immutable bounds, own cursor):
```csharp
sealed class Lease { public long LeaseId, Start, End; public int Length; public short KeyVersion; public long Cursor; } // Cursor starts at Start
```
`_current` is **never null**: it starts as a sentinel `Lease.Empty` with `Start = End = Cursor = 0`.

**Taking a number** (lock-free fast path):
1. `var lease = Volatile.Read(ref _current);`
2. `long n = Interlocked.Increment(ref lease.Cursor) - 1;`
3. If `n < lease.End` → return `(lease, n)`.
4. Otherwise slow path: `await _swapLock.WaitAsync()`; if `_current` is no longer `lease` → release, go to 1; if `_next` is available → swap it in, trigger a prefetch, release, go to 1; otherwise release, await the prefetch task (up to 2 s), go to 1. On timeout → `503` with `Retry-After: 2`.

Guarantees (stress test: 64 threads across 3 lease swaps, starting from the sentinel): each number at most once; never outside a lease; exactly one swap per exhausted lease; a failed prefetch never touches the current cursor.

**Prefetch**: single-flight; at startup (after the fingerprint check), at 50% use, or when `_next` is null after a swap. One fresh `requestId` per needed lease, reused across retries of that fetch (backoff with jitter, 100 ms → 30 s).

**Faulted**: a lease with `keyVersion ≠ 1`, an unsupported length, or `409 key-mismatch` → state `Faulted`, stop leasing, critical log, `/health/issuer` shows it; creation `503` until restart with a fixed configuration.

**Metrics**: `codes_issued_total{length}`, `lease_fetch_duration`, `lease_remaining`, `lease_exhausted_total`, `codes_skipped_reserved_total`, `code_integrity_failures_total`, `cycle_walk_iterations`.

### 7.2 Creating a link (`POST /api/v1/links`)
Served on `Hosts:Api` only. Request `{ url, alias?, expiresAt? }`, optional header `Idempotency-Key` (1–128 printable ASCII characters). Every numbered step is at the top level; only step 10 branches. One method per phase.

#### Phase A — identify the request (no business rules)
1. **Identify the creator**: a valid JWT or API key → account creator (API keys need scope `links:write`); **no credentials** → guest creator with `guestCreatorKey` (§7.12); invalid or expired credentials → `401` (never silently downgrade to guest).
2. **Parse and canonicalise** without plan, time or availability rules: `normalizedUrl` (§7.6 normalisation; syntax errors → `400`), `alias` as given, `requestedExpiresAt` = the sent value in UTC truncated to milliseconds, or none.
3. **Request hash** (if an idempotency key is given): SHA-256 over UTF-8 of `normalizedUrl + "\n" + (alias ?? "") + "\n" + (requestedExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", InvariantCulture) ?? "")`.
4. **Idempotency fast path**: `SELECT` the key. `done` + same hash → the stored response, **whatever the current plan, quota, clock, freeze or availability**. `done` + different hash → `422`.

#### Phase B — stateless validation and preparation
5. **Business validation**: creation-faulted (§5.4) → `503`. Guest: `Guests:Enabled` false → `401`; CAPTCHA token required and verified (§7.12) — if verification fails only because the token was already used and the request carries an `Idempotency-Key`, the error is deferred: the request continues to the claim (step 8), returns the stored or awaited response when another request owns the key, and returns `400 captcha-invalid` only if it owns the claim itself; expiry rules of the guest tier. URL rules (§7.6), then the **destination blocklist** → `400 destination-blocked`, then the **reputation check** (§7.15) → `400 destination-unsafe`. Alias: syntax, reserved words, denylist, `app.blocked_aliases`, **generator reachability** (§4.5) → `409 alias-unavailable`. `expiresAt` = `requestedExpiresAt` or now + plan default; in the future and ≤ now + plan maximum → else `400`. `purgeAfter = expiresAt + quarantine`. `targetHost` from §7.6.
6. **Generated code only — take the number now**, before any transaction: `(lease, n) = issuer.Take()` (may wait up to 2 s for a lease); `code = Base62.Encode(Permute(lease.Length, 1, n), lease.Length)`; if `IsReserved(code)` → `codes_skipped_reserved_total++`, take again. If the request later fails, this number is simply skipped (allowed).

#### Phase C — one transaction (no external calls inside it)
7. **Open the transaction** on the `main` data source; `SET LOCAL lock_timeout = '3s'` (§7.0).
8. **Idempotency claim** (only with a key): `INSERT INTO app.idempotency_keys … VALUES (…, 'pending') ON CONFLICT DO NOTHING RETURNING 1`. Row → this request owns the key. No row → PostgreSQL made this statement wait for the other transaction; `SELECT` the row: different hash → `422`; `done` → the stored response.
9. **Quota, rate limit and alias limits** for the new operation (guest limits per §7.12), acquired here (never as endpoint middleware), so concurrent duplicates of one keyed request never compete for tokens. Rejected → roll back (the `pending` row disappears; a `429` is never stored) and return `429` (`quota`, `rate-limit` or `alias-limit`).
10. **Alias only — claim the alias in the database**:
    1. **Restore freeze** (§7.11): an active freeze → roll back, `409 alias-frozen` with `until`.
    2. **Serialise on the alias**: `SELECT pg_advisory_xact_lock(hashtextextended('zaagl.alias:' || @alias, 0))` under the transaction's 3 s lock timeout. Timeout (SQLSTATE `55P03`) → roll back, `503 alias-busy`, `Retry-After: 1`. The lock timeout stays in force for the rest of the transaction.
    3. **Re-check the block lists under the lock**: `app.blocked_aliases` and the denylist again (they may have changed since Phase B; blocking an alias takes the same advisory lock, §7.15) → `409 alias-unavailable`.
    4. **Inspect any existing row**: `SELECT … FROM app.links WHERE code = @alias FOR UPDATE`.
       - Exists, state `purgeable` → purge it now with `PurgeLink` (§7.8) inside this transaction (alias release never waits for email or the purge job).
       - Exists, same owner, not `purgeable` → roll back, `409 alias-already-yours` (renew it instead). Guests have no owner identity: an existing non-purgeable row is always `409 alias-unavailable` for a guest.
       - Exists, another owner, not `purgeable` → roll back, `409 alias-unavailable`.
       - No row → continue.
10b. **Destination fence** (both kinds): `SELECT pg_advisory_xact_lock_shared(hashtextextended('zaagl.blocklist', 0))`, then re-check `@targetHost`/`@url` against `app.blocked_destinations` (indexed lookup) → roll back, `400 destination-blocked`. Adding a blocklist entry takes the same lock **exclusively** (§7.15), so it waits for creations already past this point; its takedown sweep, which runs after it commits, sees their rows.
11. **Insert** (both kinds) with a **parameterised Npgsql command** on the transaction's connection (**not** `Database.SqlQuery`):
    ```sql
    INSERT INTO app.links (code, is_custom, code_length, code_number, key_version, lease_id, target_url, target_host,
                           owner_id, guest_creator_key, created_at, expires_at, purge_after, safety_checked_at)
    VALUES (…) ON CONFLICT ON CONSTRAINT uq_links_code DO NOTHING RETURNING id;
    ```
    - Row returned → continue.
    - No row → read the committed conflicting row and **classify** (never assume it cannot happen): alias + same owner → `409 alias-already-yours`; alias + other owner → `409 alias-unavailable`; alias + generated row → critical `namespace_violation`, `500`; generated + any row → critical `code_integrity_failure`, `code_integrity_failures_total++`, `500`, no silent retry. Roll back in every case.
    - `PostgresException` `23505` on `uq_links_number` → **allocator integrity failure**, critical, roll back, `500`.
12. **Complete the idempotency record** (only with a key): `status = 'done'`, status code and response body.
13. **Commit.** The cache invalidation `NOTIFY` (§7.3) is sent inside the transaction and is delivered on commit.

#### Phase D — response
14. `201` with the link resource (§7.9). `shortUrl` = `Links:ShortBaseUrl` (`https://zaa.gl`) + `/` + `code`, **never** built from the request `Host` header.

**Tests**: 20 concurrent creations of one unused alias by the same owner (different keys) → exactly one `201`, nineteen `409 alias-already-yours`, no `500`; two owners sending 10 each → one `201`, the winner's others `alias-already-yours`, the other owner's `alias-unavailable`; 50 concurrent requests for one alias with an artificially slow transaction → none waits more than ~3 s (`503 alias-busy`); 10 concurrent requests with the same idempotency key and one rate-limit token left → ten identical `201`s; a slow coordinator stub (5 s per lease) under creation load does not raise redirect p95 above target.

Target (server-side, §8.3 profile): p95 under 20 ms for generated codes and under 30 ms for aliases **excluding** the reputation lookup; under 150 ms including a reputation lookup that misses its cache (§7.15).

### 7.3 Redirect (`GET /{code}`, `HEAD /{code}`)
- Served on `Hosts:Short` only. Reject before any I/O if the segment is reserved, is 1–3 characters long (§4.5 rule 6), or does not match `^[A-Za-z0-9_-]{4,64}$` → `404`.
- Look up by `code` on the **`redirects`** data source, `AsNoTracking` projection; compute the state (§4.3) with the request time.
- **Cache**: `IMemoryCache` with `SizeLimit` = `Cache:MaxBytes` (default 256 MB); **every entry's `Size` is its approximate byte size** (row fields, mainly `target_url`), and every entry has a TTL: `active` → 60 s, never past `expires_at`; `404` → 10 s; `410` → 60 s, never past `purge_after`. Entries store row fields; the state is **recomputed on every hit**. Eviction under pressure is normal.
- **Invalidation** (`ILinkCacheInvalidator`): `PgNotifyLinkCacheInvalidator` from day one. Every transaction that changes a link (create, update, renew, delete, disable/enable of a link or user-with-links, takedown, safety disable, purge, account deletion) executes `SELECT pg_notify('zaagl_link_cache', @payload)`, where the payload is a JSON array of codes **at most 4,000 bytes** in UTF-8 (PostgreSQL's limit is under 8,000 bytes); bulk changes send as many notifications per chunk as needed. PostgreSQL delivers them on commit to every API instance, which removes the entries.
- **Listener liveness**: every instance sends `pg_notify('zaagl_link_cache_ping', <nodeId:counter>)` every 5 s on its own connection and expects to receive it back. If the listener connection drops or no ping returns within 15 s, the instance **clears its cache and bypasses it** (every redirect reads the `redirects` pool) until the listener is healthy again; a metric and alert track this. Worst-case exposure after a disable is therefore about 15 s on a stalled instance, and immediate otherwise. Year one has one instance, but the same code is correct for N.
- `active` → **`302`** only (a `301` is cached permanently by browsers and could not be withdrawn at expiry or takedown), `Location`, `Cache-Control: private, max-age=0`. Other states return a small static HTML page (no external assets, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`) with the status of §4.3: `404` "not found", `410` "expired" (quarantined or deleted), `410` "disabled", and for `disabled_kind` `abuse` or `safety` a "disabled for violating the acceptable use policy" page linking to `Links:ReportPageUrl` (default `https://app.zaa.gl/report`). `HEAD` returns the same status without a body. The short host never sets cookies.
- **Rate limits** (behind an interface), keyed per IP partition: 300 redirects per minute; misses (404/410) **tiered**: 30 per minute per IPv6 /64 or IPv4 address, 120 per /56, 300 per /48 or IPv4 /24 → `429`. `HEAD` counts like `GET`. A global miss-rate breaker (`Redirects:GlobalMissesPerMinute`) returns `429` to the noisiest partitions and raises an alert; a miss-rate metric and alert ship at launch (enumeration, §5.3).
- **IP partition key** (used by **every** IP-based limiter, including auth): IPv4 → the full address; IPv6 → the **/64** prefix. The client IP comes from the forwarded-header chain of §7.13; nothing else may set it.
- **Clicks**: count `GET` requests that returned `302`; `HEAD` never counts. Bots (configurable user-agent list) only increment `link_daily_stats.bot_clicks` and never create raw events or move `click_count`. **Guest links**: only `click_count` and `last_clicked_at` move (an aggregate for abuse detection, visible to admins only); no raw events, no daily stats.
- Target (server-side, §8.3 profile): p95 under 5 ms on a cache hit, under 15 ms on a miss.

### 7.4 Accounts, plans, quotas, renewal, authorization
**Accounts** (all on `Hosts:Api`)
- **Register** `{ email, password, acceptTos: true, tosVersion }` with a CAPTCHA token (§7.12 verifier): Identity password rules (min 10 characters). **Always `202`** with the same body, so the response never reveals whether the address has an account:
  - new address → create the user **unconfirmed**, a `user_profiles` row with `tos_version`/`tos_accepted_at`, and queue `confirm-email` in the same transaction;
  - address exists and is **unconfirmed** → restart the registration: replace the password hash, rotate the security stamp, queue a new `confirm-email`;
  - address exists and is **confirmed** → queue `account-exists` ("someone tried to register with your address; sign in or reset your password").
  Sends to one address are capped at 3 per 24 h across all kinds above, plus a global ceiling `Accounts:ConfirmationsPerHour`.
- **An unconfirmed account cannot sign in**: login checks the password first, then answers `403 email-unconfirmed` (only after a correct password). Unconfirmed accounts older than `Accounts:UnconfirmedTtlDays` (7) are deleted by a daily job.
- **Email confirmation**: the email links to `Hosts:Api/account/confirm-email#u=<userId>&t=<token>` (token valid 3 days). Tokens travel in the URL **fragment**, so they never reach the server log, the proxy log or a `Referer`. The page's script `POST`s them to `/api/v1/auth/confirm-email`; a link scanner that only `GET`s the page confirms nothing. `POST /api/v1/auth/resend-confirmation { email }` needs no sign-in, always returns `202`, and is limited like registration.
- **Password reset** (confirmed addresses only): `POST /api/v1/auth/forgot-password { email }` always returns `202` (no account enumeration; equal work on both paths); if the account exists and is confirmed, queue a reset email (max 3 per address per 24 h) linking to `Hosts:Api/account/reset-password#u=…&t=…` (token valid 1 hour, fragment as above). `POST /api/v1/auth/reset-password { userId, token, newPassword }` → `204`; revokes all refresh-token families **and all API keys**, bumps the security stamp, and queues a `security-notice`.
- **Account pages** (`/account/*`) send `Content-Security-Policy: default-src 'none'; script-src 'nonce-…'; style-src 'nonce-…'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`. The reverse proxy logs paths without query strings.
- Changing the email address of a confirmed account is out of scope.
- **Access token**: JWT, 15 minutes, claims `sub`, `email`, `role`, `sstamp`, header `kid`; signed with the newest key in `JWT_SIGNING_KEYS`, validated against all of them, with `alg`, issuer and audience checked explicitly; `sstamp` validated against the user (cached 60 s). The plan is read from the database whenever limits apply.
- **Refresh token**: 32 random bytes, base64url, 30 days, rotated on every use, with reuse detection: `/refresh` runs `UPDATE … SET used_at = @now WHERE token_hash = @h AND used_at IS NULL AND revoked_at IS NULL AND expires_at > @now RETURNING …`; no row but the token exists with `used_at` set: if `used_at` is less than 30 s ago and the family is not revoked → `409 refresh-raced` (a parallel refresh from the same session; the caller retries with its newer token) and nothing is revoked; otherwise → revoke the family, `401`, security log. Logout revokes the family. `POST /api/v1/auth/logout-everywhere` revokes every family and every API key.
- **Login protection**: failed passwords are limited per **(account, IP partition)**: after 5 failures that pair waits 15 minutes; across all partitions an account adds a progressive delay (1 s per recent failure, capped at 10 s) instead of a hard lockout, so nobody can lock another user (or the admin) out. Auth endpoints are also rate-limited per IP partition (§7.3): 10 per minute.
- **Admin accounts**: bootstrapped once from `ADMIN_BOOTSTRAP_EMAIL` / `ADMIN_BOOTSTRAP_PASSWORD` (marked confirmed). The Admin role **requires TOTP** (Identity authenticator): an Admin token is only issued after the second factor, and an admin without an enrolled authenticator receives, after a correct password, an **enrol-only token** (10 minutes) valid only for `POST /api/v1/auth/totp/enroll` (returns the authenticator key and QR URI) and `POST /api/v1/auth/totp/confirm { code }`; after confirmation the admin signs in normally with `totpCode`. `/api/v1/admin/*` additionally accepts requests only from `Admin:AllowedNetworks` (configuration; office or VPN ranges), checked on the client IP of §7.13. New users get plan `free`.
- **API keys** (**premium only**): `Authorization: ApiKey <key>`; scopes `links:read`, `links:write`, `stats:read`. Only a premium user can create one (`403 plan-required` otherwise). Every API-key authentication also checks that the owner's plan is still `premium` (read with the profile, cached at most 60 s) and rejects the key otherwise. Creating a key queues a `security-notice` email. Keys are revoked by password reset, `logout-everywhere`, user disable, account deletion and **a plan change from premium to free** (in the plan-change transaction, with a `security-notice` email); upgrading again does not restore them.

**Disabling a user** (admin, `POST /api/v1/admin/users/{id}/disable { reason, disableLinks: bool }`). The first transaction does the "Always" steps; with `disableLinks` the links are then disabled in further transactions of at most 1,000 rows:
- Always: set `user_profiles.disabled_at/disabled_reason/disabled_with_links`, revoke all refresh-token families and API keys, bump the security stamp, cancel the user's pending outbox emails. Disabled users receive **no emails** except `security-notice` and cannot log in.
- `disableLinks = true`: every link of the user that is not `deleted`, not `purgeable` and not already disabled gets `disabled_at = @now`, `disabled_kind = 'user-disabled'`, in chunks of 1,000 rows locked in id order (§7.0); the profile flag is set in the first chunk's transaction and later chunks are idempotent (they filter on `disabled_at is null`). Cache invalidated per chunk.
- `disableLinks = false`: links keep working until they expire.
- **Enable** (`POST /api/v1/admin/users/{id}/enable { restoreLinks: bool }`, default true): clears the user's disabled fields; with `restoreLinks`, clears `disabled_at/disabled_kind/disabled_reason` **only** on links whose kind is `'user-disabled'` (links disabled individually, for abuse or by the safety check stay disabled), chunked the same way.
- Purge, quarantine and expiry continue normally while disabled.

**Quotas and alias limits** (new creations only, after the idempotency claim, §7.2 step 9; same for both plans unless configured):
- `Quota:LinksPerDay` (default 1,000) per user, counted over the last 24 hours. Token bucket: 60 creations per minute per user. Concurrent requests may overshoot by a few; documented.
- Aliases: **active aliases** (custom links not `purgeable`, including deleted and quarantined ones, because they still block the name) < `Aliases:MaxActivePerUser` (500), and **aliases created in the last 24 hours** < `Aliases:MaxNewPerDay` (50). Counted with the `(owner_id, is_custom, created_at)` index inside the transaction.

**Renewal** — `POST /api/v1/links/{code}/renew` body `{ expiresAt? }`, one transaction:
1. `SELECT … FROM app.links WHERE code = @code FOR UPDATE`. No row → `404`.
2. Authorize (owner or admin). Compute the state with `@now`: only `active` and `quarantined` are renewable; `purgeable` → `404`; `deleted`/`disabled` → `409`.
3. New `expiresAt` = requested or now + plan default; must be **later than the current `expires_at`** (else `400 renewal-must-extend`), in the future, and ≤ now + plan maximum. New `purge_after = expiresAt + quarantine`.
4. `UPDATE app.links SET expires_at, purge_after, expiring_notice_queued_at = NULL, expired_notice_queued_at = NULL, purge_notice_queued_at = NULL`.
5. Commit; invalidate the cache; return `200`. Renewal never calls the coordinator and works during an alias freeze.

`PATCH /api/v1/links/{code}` changes `targetUrl` only (state `active`). It runs the URL rules, blocklist and reputation check of §7.2 step 5 **before** opening the transaction, then, inside the transaction, takes the destination fence of §7.2 step 10b and re-checks `app.blocked_destinations`, and updates `target_url`, `target_host` and `safety_checked_at` under the row lock, re-checking that the state is still `active`.

**Authorization** (each row has a test):

| Endpoint | Who |
|---|---|
| `GET/HEAD /{code}` (short host), `/account/*` pages, `/.well-known/security.txt` | anyone |
| `POST /api/v1/auth/*` | anyone (rate-limited, CAPTCHA where §7.4 says), except `logout-everywhere` (authenticated) |
| `POST /api/v1/reports` | anyone (CAPTCHA, rate-limited, §7.15) |
| `POST /api/v1/webhooks/email` | the email provider (signature with `EMAIL_WEBHOOK_SECRET`) |
| `POST /api/v1/links` | authenticated (`links:write`), or anyone as a guest (§7.12) |
| `DELETE /api/v1/me`, `GET /api/v1/me/export` | the user themself, **JWT only** (§7.14) |
| `GET /api/v1/links`, `GET /api/v1/links/{code}` | owner (`links:read`) or Admin |
| `PATCH`, `DELETE /api/v1/links/{code}`, `POST …/renew` | owner (`links:write`) or Admin |
| `GET /api/v1/links/{code}/stats`, `/api/v1/archive*` | owner (`stats:read`) or Admin |
| `/api/v1/keys*` | the user themself, **JWT only**; creating a key also requires plan `premium` |
| `/api/v1/admin/*` | role `Admin`, **JWT with TOTP**, from `Admin:AllowedNetworks` only |

A non-owner gets `404` for someone else's link. Guest links have no owner: every management endpoint returns `404` for them, except the admin ones. The authorization tests are **generated** over every route × principal (anonymous, guest, other user, owner with each API-key scope, owner JWT, admin JWT without TOTP, admin JWT with TOTP, admin from outside the allowed networks) and are part of the traceability table (§12).

### 7.5 Click analytics pipeline
- Bounded `Channel<ClickEvent>`, capacity 100,000, `BoundedChannelFullMode.DropWrite`, created with the **`itemDropped` callback**, which increments `clicks_dropped_total`.
- Before enqueueing: the client IP is used only to derive `country` (if a GeoIP database is configured) and is then discarded; nothing derived from the address is stored. Referrer → host only. User agent → family only. Bot hits become counter increments, not events. Raw events beyond `Analytics:RawEventsPerLinkPerHour` for a link become counter increments too.
- **Contract: analytics are best effort.** Clicks can be lost when the channel is full (`clicks_dropped_total`), when a commit outcome is unknown (`clicks_lost_total`) or when the process dies before a flush (at most about one second of clicks). Counters and statistics are never described as exact in the API or the UI; the docs state the loss metrics and the expected loss rate.
- A `BackgroundService` drains every 1 s or 5,000 events, one **transaction** per batch:
  1. `SET LOCAL lock_timeout = '3s'`; `LOCK TABLE ONLY app.click_events, app.link_daily_stats IN ROW EXCLUSIVE MODE` (table locks **before** any row lock, so a queued partition DDL cannot strand the flush while it holds link rows).
  2. `SELECT id, owner_id IS NULL AS is_guest FROM app.links WHERE id = ANY(@ids) ORDER BY id FOR NO KEY UPDATE`. Rows that no longer exist are not returned: **drop their events**.
  3. Binary `COPY` into `click_events` (non-guest links only). 4. Upsert `link_daily_stats` (`clicks`, `bot_clicks`; non-guest links only). 5. Batched `UPDATE app.links SET click_count, last_clicked_at … FROM (VALUES …)` (all links, guests included). 6. Commit.
  A lock timeout keeps the batch in memory for the next attempt; it is not counted as lost.
- **Purge interaction**: `PurgeLink` locks the row `FOR UPDATE`, which conflicts with step 2. Either the flush waits and drops the events, or the purge waits (or skips with `SKIP LOCKED`) and later deletes the free owner's statistics. Interleaving test, both orders, free and premium.
- Retry up to 3 times; if a commit outcome is unknown, drop the batch, `clicks_lost_total++`. On graceful shutdown, flush for up to 5 s.

### 7.6 URL validation rules
- Normalisation (phase A): `Uri.TryCreate(…, UriKind.Absolute)`; scheme `http`/`https`; host IDN → punycode, lower-case, strip one trailing dot; `normalizedUrl = uri.AbsoluteUri`. Failure → `400`.
- Rules (phase B): length ≤ 4,096; no control characters or whitespace; no user info; host not `zaa.gl`, not in `Links:OwnHosts` (default: `zaa.gl`, `api.zaa.gl`, `app.zaa.gl`, `notify.zaa.gl` and the staging hosts), not a subdomain of either; not a known public URL shortener or open redirector (`Safety:DenyRedirectorHosts`, seeded list, so links cannot be chained to hide a destination); IP-literal hosts in private, loopback and link-local ranges rejected, including IPv4-mapped IPv6 and decimal, octal or hex IPv4 forms (configurable). Ports allowed; fragments kept.
- `target_host` = the normalised host (punycode, lower-case, no trailing dot); used by the blocklist (§7.15), takedowns and re-checks.
- Property tests cover IDN, trailing dots, mapped and numeric IP forms, subdomains of own hosts, user info and every denied redirector host.

### 7.7 Health endpoints (API)
- `GET /health/live`: process up.
- `GET /health/ready`: database reachable on both data sources. **Never depends on the coordinator, a lease or the fingerprint check.**
- `GET /health/issuer`: `KeyUnverified`, `KeyMismatch`, `Waiting`, `Ok`, `Faulted`; monitoring only.
- Health endpoints are reachable on the internal networks only; the reverse proxy does not route `/health/*` from the internet.

### 7.8 Background jobs
Jobs run on every instance. A job that finishes in **one** transaction takes a transaction-scoped advisory lock (`pg_try_advisory_xact_lock`) inside it. A job that needs **several** transactions never holds an advisory lock across them; it claims work rows with `FOR UPDATE SKIP LOCKED` in short transactions (purge, notifications per user and stage, dispatcher claim columns, re-checks), so concurrent instances split the work and never repeat it.

**Purge routine** `PurgeLink(row)` — used by the purge job and by alias creation (§7.2 step 10.3), always inside a transaction holding the row lock:
- Re-check under the lock: state is `purgeable`.
- Guest link → nothing to archive (no statistics exist).
- `disabled_kind` `abuse` or `safety` → copy to `app.abuse_evidence` (if not already there) before deleting; the alias is already in `app.blocked_aliases` (§7.15).
- Owner plan `premium` at purge time (and account not deleted) → insert into `app.link_archive`; keep its `link_daily_stats`. Owner plan `free`, or a deleted account → delete its `link_daily_stats`.
- Delete the row. Invalidate its cache entry after commit.
- A later downgrade does not delete archived statistics; an upgrade does not restore deleted ones (`docs/DECISIONS.md`).

**Purge job** (every 15 minutes): repeat short transactions of at most 200 rows until a batch returns fewer than 200:
```sql
SELECT l.* FROM app.links l
WHERE l.purge_after <= @now
ORDER BY l.purge_after
LIMIT 200
FOR UPDATE OF l SKIP LOCKED;
```
then `PurgeLink` for each in the same transaction. **Purge happens on time** for every link (product decision v9.2): nothing waits for an email. Alert when the oldest `purge_after` still present is more than 1 hour old.

**Expiry notifications** — once a day at `Notifications:DailyRunUtc` (§7.10).

**Restore-freeze sync** — every minute: read `restoreDetectedAt` from coordinator `GET /v1/status`; if set and no `app.alias_freezes` row has `source_ref = 'coordinator:' + restoreDetectedAt`, insert a freeze `[restoreDetectedAt, restoreDetectedAt + RestoreFreezeDays)` (§7.11).

**Destination re-check** — every 6 hours (§7.15).

**Unconfirmed accounts** — daily: delete Identity users that are unconfirmed and older than `Accounts:UnconfirmedTtlDays`, with their profile rows (they own nothing else).

**Deleted accounts** — daily: remove `user_profiles` tombstones (§7.14) that no longer own any link.

**Other**: hourly (with retry; the call is a no-op when nothing is due) `SELECT app.ensure_click_partitions(@today, 3)` and `SELECT app.drop_expired_click_partitions(@today)`, scheduled outside the backup window; email-outbox dispatcher (§7.10); idempotency-key cleanup (> 24 h); refresh-token cleanup (expired > 30 days); outbox cleanup (`sent`/`cancelled`/`failed` older than 30 days); `abuse_evidence` cleanup (past `keep_until`).

### 7.9 API contracts
JSON (camelCase), ISO 8601 UTC. Errors: RFC 7807 with a stable `type` (`https://zaa.gl/problems/<name>`). Lists: `?limit=1..100 (default 50)&cursor=<opaque>` → `{ items, nextCursor }`. OpenAPI at `/openapi/v1.json`.

**Link resource**: `{ code, shortUrl, targetUrl, isCustom, isGuest, createdAt, expiresAt, purgeAfter, state: "active"|"quarantined"|"disabled", disabledKind?: "admin"|"user-disabled"|"abuse"|"safety", clickCount, lastClickedAt }`. Admin view adds `ownerId`, `guestCreatorKey` (hex), `deletedAt`, `disabledReason`, `targetHost`, `safetyCheckedAt` and all states.

| Method & route | Request | Success | Errors |
|---|---|---|---|
| `POST /api/v1/auth/register` | `{ email, password, acceptTos, tosVersion }` + `Captcha-Token` | `202` (always the same body) | `400` (validation, `captcha-required`/`captcha-invalid`, `tos-required`); `429` |
| `GET /account/confirm-email` | token in the URL fragment | `200` page that POSTs the token | — |
| `POST /api/v1/auth/confirm-email` | `{ userId, token }` | `204` | `400` (invalid or expired) |
| `POST /api/v1/auth/resend-confirmation` | `{ email }` + `Captcha-Token` | `202` (always) | `429` |
| `POST /api/v1/auth/forgot-password` | `{ email }` + `Captcha-Token` | `202` (always) | `429` |
| `GET /account/reset-password` | token in the URL fragment | `200` page with a form that POSTs to the endpoint below | — |
| `POST /api/v1/auth/reset-password` | `{ userId, token, newPassword }` | `204` | `400` |
| `POST /api/v1/auth/login` | `{ email, password, totpCode? }` | `200 { accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt }` | `401` (wrong credentials, or a pair in its wait period: same response); after a correct password only: `403 email-unconfirmed`, `403 account-disabled`, `401 totp-required` (Admin with an authenticator), `200 { enrolToken }` (Admin without one) |
| `POST /api/v1/auth/refresh` | `{ refreshToken }` | `200` same shape | `401`; `409 refresh-raced` (retry with the newer token) |
| `POST /api/v1/auth/totp/enroll` | — (enrol-only or normal token) | `200 { authenticatorKey, otpauthUri }` | `401` |
| `POST /api/v1/auth/totp/confirm` | `{ code }` | `204` | `400` wrong code |
| `POST /api/v1/auth/logout` | `{ refreshToken }` | `204` | — |
| `POST /api/v1/auth/logout-everywhere` | — | `204` (all refresh families and API keys revoked) | — |
| `GET /api/v1/me` | — | `200 { userId, email, plan, limits: { linksPerDay, aliasActiveMax, aliasNewPerDay, defaultExpiryDays, maxExpiryDays, quarantineDays, statsKeptAfterPurge, apiKeys }, aliasFreezeUntil? }` | — |
| `DELETE /api/v1/me` | `{ password }` | `204` (§7.14) | `401` wrong password; `409` last admin |
| `GET /api/v1/me/export` | `?format=json\|csv` | `200`, streamed (§7.14) | `429` (1 per hour) |
| `POST /api/v1/links` | `{ url, alias?, expiresAt? }` + `Idempotency-Key?`; no credentials = guest (§7.12) with required `Captcha-Token` | `201` link (`isGuest` for guests) | `400` (incl. `destination-blocked`, `destination-unsafe`, `captcha-required`, `captcha-invalid`), `409 alias-unavailable`/`alias-already-yours`/`alias-frozen`, `422 idempotency-mismatch`, `429 quota`/`rate-limit`/`alias-limit`, `503` (`alias-busy`, `busy`, `guest-creation-paused`, creation unavailable) |
| `GET /api/v1/links` | paging, `?state=` | `200 { items, nextCursor }` | — |
| `GET /api/v1/links/{code}` | — | `200` link | `404` |
| `PATCH /api/v1/links/{code}` | `{ targetUrl }` | `200` link | `400` (incl. `destination-blocked`, `destination-unsafe`), `404`, `409` not active |
| `POST /api/v1/links/{code}/renew` | `{ expiresAt? }` | `200` link | `400` (incl. `renewal-must-extend`), `404`, `409` |
| `DELETE /api/v1/links/{code}` | — | `204` (an alias stays unavailable to others until `purgeAfter`) | `404` |
| `GET /api/v1/links/{code}/stats` | `from`, `to` (≤ 366 days), `granularity=day\|month` | `200 { series, topReferrers }` | `400`, `404` |
| `GET /api/v1/archive` | paging | `200 { items: [{ linkId, code, isCustom, targetUrl, createdAt, expiresAt, purgedAt }], nextCursor }` | — |
| `GET /api/v1/archive/{linkId}/stats` | as link stats | `200 { series }` | `404` |
| `POST /api/v1/keys` | `{ name, scopes[] }` | `201 { id, name, prefix, scopes, createdAt, key }` | `400`, `403 plan-required` (free plan), `409` limit of 20 |
| `GET /api/v1/keys` | — | `200 [{ id, name, prefix, scopes, createdAt, lastUsedAt }]` | — |
| `DELETE /api/v1/keys/{id}` | — | `204` | `404` |
| `GET /api/v1/admin/users` | paging, `?email=` | `200 { items: [{ userId, email, emailConfirmed, roles, plan, createdAt, disabledAt, disabledWithLinks }], nextCursor }` | — |
| `PUT /api/v1/admin/users/{id}/plan` | `{ plan }` | `204` (premium → free revokes the user's API keys, §7.4) | `400`, `404` |
| `POST /api/v1/admin/users/{id}/disable` | `{ reason, disableLinks }` | `204` | `404`, `409` yourself or already disabled |
| `POST /api/v1/admin/users/{id}/enable` | `{ restoreLinks? }` | `204` | `404`, `409` not disabled |
| `GET /api/v1/admin/links/{code}` | — | `200` admin link view | `404` |
| `POST /api/v1/admin/links/{code}/disable` | `{ reason, kind: "admin"\|"abuse" }` (reason 1–500 chars) | `204` | `404` |
| `POST /api/v1/admin/links/{code}/enable` | — | `204` | `404`, `409` deleted, `409 takedown-active` (abuse or safety) |
| `POST /api/v1/admin/links/{code}/reinstate` | `{ reason }` | `204` (audited reversal of a wrong takedown, §7.15) | `400 destination-unsafe`/`destination-blocked`, `404`, `409` not taken down |
| `POST /api/v1/reports` | `{ code?, url?, category, details?, reporterEmail? }` + `Captcha-Token` | `202` | `400`, `429` |
| `GET /api/v1/admin/reports` | paging, `?status=` | `200 { items, nextCursor }` | — |
| `POST /api/v1/admin/reports/{id}/resolve` | `{ status: "actioned"\|"dismissed", resolution }` | `204` | `404` |
| `POST /api/v1/admin/takedowns` | `{ filter: { targetHost?, guestCreatorKey?, ownerId?, createdFrom?, createdTo? }, reason, blockDestination: bool, dryRun: bool }` | `200 { linksAffected }` (§7.15) | `400` empty filter |
| `GET/POST/DELETE /api/v1/admin/blocked-destinations` | `{ kind, pattern, reason }` | `200` / `201` / `204` | `400`, `409` exists |
| `GET/POST/DELETE /api/v1/admin/blocked-aliases` | `{ code, reason }` | `200` / `201` / `204` | `409` in use by a live link |
| `GET/POST/DELETE /api/v1/admin/alias-denylist` | `{ pattern, kind }` | `200` / `201` / `204` | `400` |
| `POST /api/v1/webhooks/email` | provider bounce and complaint events, signed | `204` | `401` bad signature |
| `GET /api/v1/admin/alias-freezes` | — | `200 [{ id, startsAt, until, source, reason }]` | — |
| `POST /api/v1/admin/alias-freezes/{id}/end` | `{ reason }` | `204` (ends a freeze early; audited) | `404` |
| `GET /api/v1/admin/allocator` | — | `200` coordinator pool view | `502` |

### 7.10 Email
Sent from `notify.zaa.gl` (SPF, DKIM, DMARC `p=quarantine` or stricter, §1.2). Two kinds of email share one transactional outbox and dispatcher:
- **Account emails** (`confirm-email`, `password-reset`, `account-exists`, `security-notice`): queued by the request that triggers them; sent as soon as possible; no digest.
- **Link warnings** (`expiring`, `expired`, `purge`): only for not-disabled users (every user who can sign in is confirmed); best effort; nothing waits for them.
- **Content rules**: emails show short codes and dates, **never target URLs**. Confirmation and reset tokens are generated by the dispatcher at send time and put in the URL fragment (§7.4); the outbox payload never holds a token.
- **Suppression**: the dispatcher never sends to an address in `app.email_suppressions`. `POST /api/v1/webhooks/email` (signed) adds addresses on hard bounce or complaint. Permanent SMTP `5xx` failures are not retried and suppress the address.

**Daily digest** — the notification job runs once a day at `Notifications:DailyRunUtc` (default 06:00 UTC); each queueing transaction locks the user's `app.user_profiles` row `FOR UPDATE SKIP LOCKED` (§7.8), so instances can share the work; the partial unique index `(user_id, kind, digest_date)` is the backstop. At most **one email per warning stage per user per day** (partial unique index):

| Stage | Link qualifies when | Content |
|---|---|---|
| `expiring` | state `active`, `expires_at − 7 days ≤ now < expires_at`, `expires_at − created_at > 7 days`, `expiring_notice_queued_at` null | links that stop working on `expires_at`; how to renew; plan maximum. |
| `expired` | state `quarantined`, `expired_notice_queued_at` null | links now answering 410; deleted on `purge_after`; renewal still possible. Free: statistics deleted too. |
| `purge` | state `quarantined`, `purge_after − 7 days ≤ now < purge_after`, `purge_notice_queued_at` null | final warning: deleted on `purge_after`, on time; an alias becomes available to others. |

- A link created with an expiry shorter than 7 days skips `expiring`. Deleted, disabled and purgeable links get no warnings.
- **Queueing** (one transaction per user and stage): the `email_outbox` row, one `email_outbox_items` row per link with `expires_at_snapshot`, and the link's `*_notice_queued_at`.
- **Dispatching** (`BackgroundService`, every 30 s). **Claim** rows in a short transaction:
  ```sql
  UPDATE app.email_outbox SET status = 'sending', locked_until = @now + interval '5 minutes'
  WHERE id IN (SELECT id FROM app.email_outbox
               WHERE (status = 'pending' AND send_after <= @now) OR (status = 'sending' AND locked_until < @now)
               ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED)
  RETURNING *;
  ```
  Then, per claimed row, **outside any transaction**:
  1. Skip and `cancel` if the address is suppressed, or if the user is now disabled or deleted (any kind except `security-notice`, which carries its recipient address in the payload and is always sent).
  2. For warnings, **re-validate** each item immediately before rendering: drop it if the link is gone, its `expires_at` differs from the snapshot, or its state no longer matches the stage. No items left → `cancelled`. (Best effort: a renewal committing between this check and SMTP acceptance can still yield one outdated warning; accepted, no lock across SMTP.)
  3. Render and send through `IEmailSender`.
  4. On SMTP acceptance, one short transaction: `status = 'sent'`, `sent_at`, `locked_until = null`.
  5. On a temporary failure: `status = 'pending'`, `attempts += 1`, `last_error`, next `send_after` (1 min, 5 min, 30 min, 2 h, then every 6 h); after 3 days → `failed`, error log. A permanent failure → `failed` and the address is suppressed.
- At-least-once delivery; a rare duplicate is acceptable. "Sent" means accepted by the provider.
- Templates: plain text + simple HTML, English, in `src/Zaagl.Api/Email/Templates`.

### 7.11 Alias restore freeze
Purpose: after a database restore, links created after the backup are lost. Without protection, someone could claim a lost alias before its owner notices. During a freeze **no new alias can be claimed by anyone**; everything else works.

- A freeze is a row in `app.alias_freezes`. A freeze is **active** when `starts_at ≤ now < until`. The API caches the active-freeze check for 60 s; on a cache miss it queries the table.
- **Sources**:
  - *Automatic*: the coordinator sets `restore_detected_at` when it sees the allocator behind the HWM by more than one reservation (§6.4 row 3); the restore-freeze sync job (§7.8) turns it into a freeze. This detection is a heuristic and can miss a restore (for example one within a few leases of the HWM).
  - *Operator* (authoritative): `docker compose run --rm ops declare-restore --reason "…"` inserts a freeze starting now for `RestoreFreezeDays`. **Mandatory step** of the restore procedure (§8.1), so the freeze never depends on the heuristic.
- Admins can list freezes and end one early (audited).
- Alias creation returns `409 alias-frozen` with `until`; `/api/v1/me` shows `aliasFreezeUntil`.
- Generated links, renewals (including of aliases), deletes and redirects are unaffected.

### 7.12 Guest creation
Requests to `POST /api/v1/links` without credentials create **guest links** (product decision, §1.1). Settings `Guests:*`; `Guests:Enabled` (default true) is a kill switch.

**Creator key.** Guests are identified only by their IP partition (§7.3: IPv4 address, IPv6 /64), never by a stored IP: `guestCreatorKey = HMAC-SHA256(GUEST_KEY, UTF8(tag + partition))`, truncated to 16 bytes, with a length tag so keys of different prefix sizes never collide: `"v4:" + address`, `"v6/64:" + prefix`, and for the wider quotas `"v6/56:" + prefix`, `"v6/48:" + prefix`. Stored on the link (`guest_creator_key`) and used as the idempotency scope. Changing `GUEST_KEY` only resets guest quotas; it never affects links.

**Limits** (checked inside the transaction at §7.2 step 9, like account limits):
- **20 links per rolling 24 hours** per creator key (`Guests:LinksPerDay`), aliases included. **Guest limits are hard**: inside the creation transaction, each limit (creator key, /56, /48) first locks its hourly bucket row in `app.guest_quota` (`INSERT … ON CONFLICT (prefix_key, hour) DO UPDATE SET count = guest_quota.count` to create-or-lock, then `SELECT … FOR UPDATE` of the key's buckets in key order), sums the last 24 buckets, refuses with `429 quota` at the limit, and otherwise increments; a rollback releases the claim. Concurrent requests for one key therefore serialise on its bucket. Account limits stay approximate (§7.4).
- Wider prefixes too, so one IPv6 allocation does not become thousands of quotas: **100 per 24 h per /56** and **300 per 24 h per /48** (`Guests:LinksPerDayPer56`, `Guests:LinksPerDayPer48`); counted in hourly buckets `app.guest_quota(prefix_key bytea, hour timestamptz, count int not null, primary key (prefix_key, hour))`, where `prefix_key` = HMAC-SHA256(`GUEST_KEY`, prefix) truncated to 16 bytes; the last 24 buckets are summed inside the transaction and buckets older than 25 hours are deleted hourly.
- Token bucket **5 per minute** per IP partition (`Guests:PerMinute`).
- Global ceiling `Guests:GlobalPerHour` (default 5,000), counted in memory per instance, a brake against distributed abuse: when it trips, partitions that created the most in the last hour get `503 guest-creation-paused` first, so one burst does not pause guests everywhere.

**Rules**
- Expiry: default and maximum **7 days** (`Guests:ExpiryDays`); a shorter `expiresAt` is allowed. `purge_after = expires_at` (no quarantine).
- Aliases: same syntax, reserved-word and generator-reachability rules (§4.5); the restore freeze applies.
- The response is the normal link resource with `isGuest: true` and no management hints. Guests receive no emails and have no statistics; only an aggregate click counter is kept, visible to admins (§7.3).
- Guest links cannot be renewed, edited, deleted or queried through any owner endpoint. Admins can view and disable them (`GET/POST /api/v1/admin/links/{code}…`).
- **CAPTCHA**: `ICaptchaVerifier` implemented with Cloudflare Turnstile (`TURNSTILE_SECRET`), **on by default** (`Captcha:Enabled = true`) for guest creation, registration, resend-confirmation, forgot-password and abuse reports. Requests carry the token in header `Captcha-Token` (`400 captcha-required` / `400 captcha-invalid`). Verification failures of the Turnstile service itself → `503`, never a silent pass. The server-side check (`siteverify`) also requires: `hostname` equal to the environment's app host (`app.zaa.gl`; staging's own host in staging), `action` equal to the endpoint's action name (`guest-link`, `register`, `resend-confirmation`, `forgot-password`, `report`), and a token younger than 300 s; retries of the `siteverify` call itself reuse one `idempotency_key` per request. Each environment has its own site key and secret. Test and development environments use Turnstile's documented test keys.
- A guest who later registers does **not** gain ownership of earlier guest links (no ownership proof exists).

### 7.13 Web app contract and the client-IP chain
The web app (`zaagl-web`, Next.js, on `app.zaa.gl`) is built in its own repository. This build guarantees what it relies on.

**Request paths**
| Path | Hops seen by the API | Trusted forwarders |
|---|---|---|
| Browser → `zaa.gl` (redirects) | reverse proxy → api | proxy |
| API client → `api.zaa.gl` | reverse proxy → api | proxy |
| Browser → `app.zaa.gl` → BFF → api (internal) | reverse proxy → web → api | proxy, web |
| Staging: add the access gate in front of the proxy | gate → proxy → … | gate ranges (configuration) |

- `ForwardedHeadersMiddleware` with `ForwardedHeaders.XForwardedFor | XForwardedProto | XForwardedHost`, `ForwardLimit = Network:ForwardLimit` (production 2, staging 3), and `KnownProxies` = the **exact** addresses of the proxy and the web container (fixed addresses on `edge`, §3), never whole subnets. Staging adds the gate's published ranges to `KnownNetworks`. The middleware reads `X-Forwarded-For` right to left and stops at the first untrusted address, so a value supplied by an internet client is never used.
- The BFF **appends** the address it received from the proxy to `X-Forwarded-For` (never replaces it, never forwards the leftmost entry) and passes `Captcha-Token` through. Port 8080 of `api` is not published on the host (§3).
- Tests: two visitor addresses through proxy and BFF get separate guest quotas and login limits; a forged `X-Forwarded-For` from a direct internet client does not change its partition; a request reaching `api` from an untrusted container address is partitioned by that address.

**Sessions in the browser**
- The browser never holds an access or refresh token. The BFF calls `/api/v1/auth/login` and `/refresh` server-side and stores the token pair in an encrypted, `HttpOnly`, `Secure`, `SameSite=Lax`, host-only cookie named `__Host-zaagl_session` (`Path=/`, no `Domain`). The BFF reads user details from `/api/v1/me`; it never receives `JWT_SIGNING_KEYS` or any other secret of §3. The web app has its **own** session key set (`WEB_SESSION_KEYS`, managed in the web repository, persisted across restarts and instances, rotated with an overlap), the cookie stays under 4 KB, and its lifetime matches the refresh token. Refreshes are **single-flight per session** inside the BFF; a `409 refresh-raced` from the API is retried once with the cookie's newer token.
- Every BFF route that changes state requires a CSRF token (double-submit: cookie `__Host-zaagl_csrf` plus header), because `staging.zaa.gl` and future `zaa.gl` content are the same site as `app.zaa.gl`.
- The API stays bearer-only with **no CORS**: browsers only talk to their own origin.
- The API publishes its OpenAPI document for every build (§12); the web repository generates its client from it.

### 7.14 Account deletion and data export
**Export** — `GET /api/v1/me/export?format=json|csv` (JWT only, 1 per hour): streams the profile, every link (code, target, dates, state, click count), daily statistics, archived links and their statistics, using a server-side cursor; nothing is staged on disk. JSON is one document; CSV is a ZIP of one file per entity.

**Deletion** — `DELETE /api/v1/me { password }` (JWT only; the last remaining admin cannot delete itself).
0. First transaction: `user_profiles.deleted_at = @now`; revoke API keys and refresh tokens; bump the security stamp. From here on, creation, renewal, PATCH and API-key or JWT authentication reject a profile with `deleted_at` set (checked in the handler, not only through the cached stamp).
Then in chunks of 1,000 links locked in id order (§7.0), then one final transaction:
1. Every link of the user that is not yet `deleted`: `deleted_at = @now`; `expires_at = least(expires_at, @now)`; `purge_after = least(purge_after, @now + Links:QuarantineDays)` (keeps `purge_after ≥ expires_at`; an alias stays held for the normal quarantine, so nobody can take it over immediately; a long premium expiry does not keep the data for years). Cache notified.
2. Delete the user's `link_archive` rows and their `link_daily_stats`; delete daily statistics of the user's links; raw `click_events` age out with retention (they hold no personal data, §4.4).
3. Revoke and delete API keys and refresh tokens; cancel and delete pending outbox rows; delete idempotency rows of scope `u:<userId>`.
4. Delete the Identity user (email, password hash, security stamp, TOTP secret); the profile row stays as a tombstone with no personal data until the user's last link is purged (§7.8), then it is removed.
Deletion is irreversible. The password check is the confirmation; a `security-notice` is queued in the final transaction, after step 3 and before step 4 (the dispatcher sends it to the address stored in its payload, then clears the address from the payload), so the owner learns of it.

**What is kept after deletion, and why** (stated in the `204` response documentation, the web app's confirmation screen and the privacy notice): links and aliases until their (shortened) `purge_after`, so a deleted alias cannot be taken over at once, without any link to a person; raw click events until `click_retention_months`, holding no personal data (§4.4); `app.abuse_evidence` rows for links taken down for abuse, for `Abuse:EvidenceRetentionDays`, on the basis of preventing abuse; backups until they age out of the backup retention. Everything else is deleted.

### 7.15 Abuse controls
**Destination reputation** — `IUrlSafetyCheck` implemented with the Google Web Risk Lookup API (`uris.search`, threat types malware, social engineering, unwanted software). Called in Phase B of creation and in `PATCH`. Verdicts are cached per normalised URL for `min(provider expireTime, 24 h)`. The lookup has a 400 ms timeout: on timeout or error the link is created with `safety_checked_at = null` and the re-check job picks it up at its next run that has budget (fail-open for availability, with a metric and an alert on the error rate). An unsafe verdict → `400 destination-unsafe`.

**Lookup budget** — `Safety:MonthlyLookupBudget` (default **100,000**, the provider's free tier; product decision v9.4). Lookups for create and edit come first; when the month's budget is exhausted, new links are created with `safety_checked_at = null` (the same fail-open path as an outage, product decision v9.4) and an alert fires. Re-checks get each run's share of what remains (`remaining ÷ runs left in the month`), ordered by clicks in the last 7 days first (guest links: the `click_count` increase since their last check). The month's usage is counted in one database row per month (`app.safety_budget(month date primary key, used int not null)`, incremented atomically before each lookup), so N instances share one budget. Alerts at 80% and 100% of the budget.

**Re-checks** — every 6 hours. Candidates: links that are not purgeable or disabled and have `safety_checked_at` null, or were created in the last 30 days, or were clicked in the last 7 days, and whose `safety_checked_at` is null or older than 6 hours, within the lookup budget. A short claim transaction selects a batch `FOR UPDATE SKIP LOCKED` and sets `safety_checked_at = @now` on it, then commits; lookups run **outside any transaction**, deduplicated by URL; a lookup error resets `safety_checked_at` to null for those links. An unsafe verdict disables the link (`disabled_kind = 'safety'`) through the takedown routine below.

**Destination blocklist** — `app.blocked_destinations` entries of kind `host`, `host-suffix` or `url-prefix`; checked in Phase B, in `PATCH` and by takedowns. Seeded with `Safety:DenyRedirectorHosts` (§7.6).

**Takedown routine** (used by admin link disable with `kind = abuse`, bulk takedowns and safety re-checks), per chunk of at most 1,000 links in id order (§7.0): set `disabled_at` (kept if already set), `disabled_kind` (`abuse` or `safety`, overwriting `admin` or `user-disabled`, so a later user re-enable cannot bring the link back), `disabled_reason`; for custom aliases insert the code into `app.blocked_aliases` (the alias is **never released**, even after purge); insert `app.abuse_evidence` with `keep_until = @now + Abuse:EvidenceRetentionDays`; notify the cache. Bulk takedowns (`POST /api/v1/admin/takedowns`) match by `target_host` (exact or suffix), `guest_creator_key`, `owner_id` and a creation window; at least one filter is required; `dryRun` returns the count only; `blockDestination` also adds the host to the blocklist; every run writes `app.takedowns`.

**Reports** — `POST /api/v1/reports` (CAPTCHA, 5 per hour per IP partition) stores an `abuse_reports` row and queues no email to the reporter. Admins list and resolve reports. The runbook defines the response targets: **12 hours for phishing and malware** (product decision v9.4; every report in those categories raises an immediate alert to the TechTroll team) and **48 hours** for everything else, plus the `abuse@` mailbox and the weekly review of open reports. Admins have an emergency one-step disable (`POST /api/v1/admin/links/{code}/disable` with `kind: "abuse"`, or a takedown by host) that takes effect on every instance within the R5 bound (§7.3).

**Reinstating** — `POST /api/v1/admin/links/{code}/enable` refuses links disabled with kind `abuse` or `safety` (`409 takedown-active`). A wrong takedown is reversed only with `POST /api/v1/admin/links/{code}/reinstate { reason }`: it runs the reputation lookup before the transaction, then in one transaction (under the alias lock for aliases, and the destination fence of §7.2 step 10b) re-checks the blocklist, clears the disable fields (or sets `disabled_kind = 'user-disabled'` when the owner's profile is disabled with `disabled_with_links`), removes the code from `app.blocked_aliases` if this link put it there, sends the §7.3 notification, and writes `app.takedowns` with the reversal and the admin; the evidence row is kept.

**Blocking** — adding to `app.blocked_destinations` takes `pg_advisory_xact_lock(hashtextextended('zaagl.blocklist', 0))` exclusively (the fence of §7.2 step 10b) and commits before its takedown sweep; adding to or removing from `app.blocked_aliases` takes the alias's advisory lock (§7.2 step 10). `zaa.gl/.well-known/security.txt` names the security and abuse contacts.

**Alias denylist and blocked aliases** — §4.5 rules 5 and 4.

**Signals** — metrics and alerts: creations per guest prefix and per account per hour, unsafe verdicts per hour, takedowns, open reports older than 24 hours, guest links with more than `Abuse:GuestClickAlertThreshold` clicks in an hour. The infra repository also monitors the domain's own Web Risk status and Search Console security issues for `zaa.gl`.

---

## 8. Guarantees

| # | Guarantee | Mechanism |
|---|---|---|
| R1 | A generated code is **never issued twice**, across crashes, restarts, power loss, database restores and loss of the host. | HWM before commit; archive-confirmed reservations; manifest and length markers in the off-host sink; fail-closed startup checks; operator-controlled recovery (§6.4). |
| R2 | A generated code can **never become an alias**, and the generator can **never produce an alias**, with any number of instances and after any restore. | Generator cap and reachability (§4.5); key fingerprint (§5.4). |
| R3 | Two links never share a code at the same time; an alias is free only when no row holds it or its row is `purgeable`, and it is not blocked or denylisted. | Unique constraint; per-alias advisory lock; predecessor purge (§7.2); `blocked_aliases`, denylist (§4.5). |
| R3a | An alias taken down for abuse or by the safety check is **never released**, unless the takedown is reversed with the audited `reinstate`. | `app.blocked_aliases`, written in the takedown transaction (§7.15). |
| R4 | After a database restore, no alias can be claimed for `RestoreFreezeDays`. | Operator `declare-restore` (mandatory) plus automatic detection (§7.11). |
| R5 | No link redirects after `expires_at`, whether or not it has been purged; a disabled, deleted or taken-down link stops redirecting on every healthy instance as soon as the change commits, and within about 15 s on an instance whose listener has stalled. Redirects are `302` only, so browsers do not keep a permanent copy. | Total state function; state recomputed on every cache hit; byte-bounded `NOTIFY` with listener liveness and cache bypass (§7.3). |
| R6 | A link whose creation returned `201` survives crashes, restarts and power loss until its purge. | PostgreSQL WAL, `fsync`, `synchronous_commit` on. |
| R7 | A **database restore loses links created after that backup**: those codes return `404`. A lost alias can be re-created by anyone once the freeze ends, including, but not only, its former owner. | Inherent; accepted. Recovery point = backup policy (infra repository; target: WAL archive with `archive_timeout` ≤ 60 s). |
| R8 | R1 survives loss of the host: with PostgreSQL restored to the end of the WAL archive, `resume` continues where the allocator stopped and wastes no length; if the archive cannot be fully restored, `new-length` uses the sink's markers and skips the current length. The `coordstate` volume is never rolled back to an older copy. | Archive-confirmed reservations; manifest and length markers (§6.4); runbook. |
| R9 | Link creation survives loss of the host as long as the escrowed scramble key exists. | Key escrow (§3, §5.3); quarterly drill. |

### 8.1 Database restore procedure (runbook, also tested)
1. Stop `api` (all replicas), then `coordinator`.
2. Restore PostgreSQL. **Do not touch `coordstate`.**
3. Run `docker compose run --rm ops declare-restore --reason "<incident>"`.
4. Start `coordinator`; it moves the allocator to the HWM (row 3) or blocks leasing (rows 5–7) for an operator decision.
5. Start `api`. Check `/api/v1/admin/allocator` and `/api/v1/admin/alias-freezes`.

**Host lost**: provision a new host from the infra repository; restore secrets from escrow; restore PostgreSQL from the latest base backup **and every archived WAL segment** (recovery target: end of archive); set `recovery_target_timeline` explicitly to the timeline of the lost primary's newest segment in the archive, never `latest`; `declare-restore`; start the coordinator (it blocks: row 7, or 1a); `ops recover resume` (it verifies the restore itself; if verification fails: `ops recover new-length`); wait until `/v1/status` shows leasing `ok` (if the host is lost again before that, use `new-length`); start the API; switch DNS. The quarterly drill runs this end to end on an isolated copy, checks that sample codes resolve, and records the time taken (the measured RTO). **The drill reads the production WAL archive read-only and writes its own WAL to a drill-only archive and its markers to a drill-only allocator sink** (separate buckets and credentials); production's `archive_command` and sink credentials are never used by a drill.

### 8.2 Chaos and timing acceptance tests
Two suites (§8.3 explains why): **(a) in-process scenarios** host the API and the coordinator in one test host sharing one `FakeTimeProvider`, and drive every time-dependent and race row through failpoints; **(b) container chaos** (`scripts/chaos.sh`) runs the real compose stack on the real clock for kill, reboot and restore rows. Chaos runs print their seed, guarantee a minimum number of kills per container type, and can be replayed from the seed. The **load generator records every acknowledged code, owner, target and expiry** to a local file; the checker verifies against that file.

| Event | Required behaviour |
|---|---|
| API killed with `-9` under load | Restarts, new lease, creates again within 5 s of ready. No acknowledged code changes target or owner. |
| User B claims an alias 1 second after the old link became `purgeable`, before the purge job ran | Old row purged inside B's transaction (archive/stats handled); B gets `201`. |
| Renewal and purge race on the same row | Exactly one wins; no case where renewal reports success and the row is gone. |
| Renewal requesting an earlier expiry | `400 renewal-must-extend`; nothing changed. |
| Guest creates 20 links in 24 h, then a 21st (also from another API instance and after an API restart) | 21st gets `429 quota`. |
| Guest link at `expires_at` | `404` immediately; purged without waiting; its alias can be claimed by anyone right away; no click events were ever stored. |
| Guest calls renew, patch, delete, stats or list | `401` (no credentials) or `404` for a guest link via an account. |
| Request with an expired JWT | `401`, never treated as a guest. |
| Idempotent retry after a plan downgrade, after the requested expiry passed, after the quota is used up, or during an alias freeze | Stored `201`. |
| Concurrency tests of §7.2 | As specified there. |
| API dies during a lease fetch | Lease never created, or returned again for the same `requestId`. |
| Coordinator down, or slow (5 s per lease) | Generated creation continues from leases, then `503`; alias creation, renewals and redirects unaffected; redirect p95 within target. |
| Coordinator killed at each failpoint of §6.2 and init (after HWM fsync / before COMMIT / after COMMIT before response / after marker write) | Next start finishes init (row 2) / no lease beyond the HWM; every failpoint is proven hit. |
| HWM rolled back below the database / database restored to before init / HWM lost | Leasing blocked (rows 5 / 6 / 7); `trust-database` / `reattach` / `new-length` works; id and fingerprint end identical. |
| Host lost under load: new host, PostgreSQL restored to the end of the WAL archive, `coordstate` empty | `resume` continues in the same length after the last archived reservation; no acknowledged code is ever issued again; at most one reservation is skipped. |
| Host lost, WAL restore stopped early at a missing segment | `ops recover resume` refuses (archive continuity check); `new-length` works; no code reissued. |
| Quarterly drill | Writes nothing to the production WAL archive or allocator sink. |
| Host lost, archive incomplete: PostgreSQL restored from a base backup taken before length 6 started, `coordstate` empty, sink intact | `new-length` moves to length 7; no acknowledged code is ever issued again. |
| Database from before initialisation restored after codes were issued; HWM and installation row both gone; sink intact | Startup row 1a: leasing blocked; `init` refused; `new-length` adopts the manifest identity; no code reissued. |
| Repeated `new-length` until no length remains | Alerts at two remaining; afterwards generated creation answers `507`; aliases keep working. |
| WAL archiving stopped for an hour under load | Generated creation continues until the confirmed reservation is used, then `503 reservation-pending`; aliases and redirects unaffected; resumes when archiving recovers. |
| Bulk takedown of 1,000 five-character codes and of 1,000 64-character aliases | Every chunk commits; no notification payload exceeds 4,000 bytes; every link stops redirecting. |
| Alias blocked (and, separately, a destination blocked with takedown) while a creation is paused after Phase B | The creation is refused or its link is caught by the takedown; no active blocked alias or destination exists afterwards. |
| Disable a hot cached link while an instance's listener is stalled | That instance bypasses its cache within 15 s; measured exposure ≤ 15 s. |
| Ordinary admin enable of a safety- or abuse-disabled link | `409 takedown-active`; `reinstate` works and is audited. |
| Two parallel BFF refreshes with one refresh token | One succeeds, the other gets `409 refresh-raced`; the family stays valid. |
| Turnstile token from the staging host, or for a different action, used in production | `400 captcha-invalid`. |
| 30 parallel guest creations from one /64 with 2 of 20 left | Exactly 2 succeed. |
| Allocator sink unreachable when a new length must start | Leasing blocked with `503`; buffered leases keep working; redirects and aliases unaffected. |
| Docker daemon restart and VM reboot under load | All services return without manual steps; the coordinator runs its startup check after the database is reachable. |
| Second API instance with a different scramble key | `KeyMismatch`; issues no codes, accepts no aliases; first instance and redirects unaffected. |
| PostgreSQL restored from an older snapshot (§8.1) | Codes acknowledged before the snapshot resolve as before; later ones `404`; no generated code reissued; alias creation `409 alias-frozen` for 30 days; a lost alias claimable after the freeze. |
| Restore within one reservation of the HWM (heuristic misses it) | The operator freeze from step 3 is active anyway. |
| Any attempt to claim a generated code (active, quarantined, purged, or lost in a restore) as an alias | `409 alias-unavailable`. |
| Link reaches `expires_at` while cached as active | Next hit returns `410`. |
| Free and premium links purged | Free: stats deleted. Premium: archive row and stats kept. |
| Email provider down for 2 days during the purge window | Purge happens at `purge_after` regardless; warnings retry and are dropped once their links are gone. |
| Free user creates an API key; premium user with keys is downgraded | `403 plan-required`; on downgrade every key stops working at once (revoked in the plan-change transaction) and a `security-notice` is sent; a key used within the 60 s profile cache after the downgrade is still rejected because the revocation is checked directly. |
| Unconfirmed account | Cannot sign in (`403 email-unconfirmed` after a correct password); deleted after 7 days; re-registering the address restarts the registration. |
| Account deleted with active links, aliases and a premium archive | Links answer `410`; aliases held until `min(purge_after, now + quarantine)`; archive and stats gone; Identity row gone; tombstone removed after the last purge. |
| Destination flagged unsafe after creation | Next re-check disables the link (`safety`); the alias goes to `blocked_aliases` and is never released; evidence row kept. |
| Bulk takedown by host during traffic, 10,000 links | Done in chunks without blocking redirects; every instance stops redirecting them within a second of each chunk commit. |
| Forged `X-Forwarded-For` from an internet client; two visitors through the BFF | The forged value is ignored; the two visitors get separate quotas. |
| Analytics flush and purge interleaved (both orders, free and premium) | Free: no stats remain. Premium: archive and all committed clicks kept. |
| Renewal after a warning was queued, before it was sent | Stale item dropped at send time. |
| Links of one user reach different stages at different hours | At most one email per stage per user per day. |
| Admin disables a user with links, then re-enables with restore | Links answer `410` while disabled; afterwards only `user-disabled` links come back; individually disabled links stay disabled; no emails while disabled. |
| 1,000,000 distinct unknown codes requested from many partitions (the load generator uses trusted forwarded addresses) | Memory stays bounded by `Cache:MaxBytes`; tiered miss limits and the global breaker engage; the enumeration alert fires. |
| Requests rotating through addresses of one IPv6 /64 | Share one rate-limit partition. |
| 10 registrations for the same email address | At most 3 confirmation emails to it per 24 h. |
| Partition maintenance with the fake clock moved 14 months ahead (test database) | Partitions follow `p_today`; with `test_clock_allowed = false` the call is rejected. |

Global invariants after every scenario. They must be able to fail, so they check what constraints cannot: `code_integrity_failures_total`, `namespace_violation` and allocator-integrity log counts are zero; no `5xx` except documented `503`s; every generated row's `code_number` lies inside its lease and `code = Encode(Permute(length, 1, code_number), length)`; **no code appears twice in the whole acknowledged log, including codes lost in a restore**; the HWM read from disk ≥ every lease end; no alias link is generator-reachable; no generated code matches a reserved word; no row past `expires_at` redirects.

### 8.3 Test seams and determinism
- **Failpoints**: `IFailpoints` with a no-op implementation registered in production; tests register one that can pause, crash or throw at named points: coordinator after HWM fsync / before COMMIT / after COMMIT before response / after a length marker; issuer after the lease request is sent / before its response is read; `PurgeLink` after the row lock / before DELETE; analytics flush after the link locks / before COPY; renewal after the lock / before UPDATE; takedown chunk after UPDATE / before COMMIT. Every "both orders" and "any point" test drives each named point and asserts it was hit (a counter), so a test cannot pass without exercising the order. A startup check fails if a non-no-op failpoint implementation is registered outside the `Testing` environment.
- **Clock policy**: `TimeProvider` is the business clock (expiry, quotas, purge, freezes, digests, partitions). Timeouts, backoff and token buckets use real time and are configurable. Time-dependent scenarios run in-process with one `FakeTimeProvider` shared by the API and the coordinator; background loops wait on `TimeProvider`-based delays so advancing the fake clock drives them. **No shipped image contains any way to set the clock at runtime.**
- **Durability seam**: the HWM store writes through `IHwmFileSystem`. A crash-simulation test records every write, fsync, rename and directory fsync, replays every prefix while discarding unsynced data, and asserts that startup either recovers to HWM ≥ every committed lease or blocks. Configuration tests assert `fsync = on`, `synchronous_commit = on`, `full_page_writes = on`. Traceability labels power-loss coverage "simulated".
- **Performance profile**: the latency targets are measured server-side from the OTel histograms on a host shaped like staging (not on shared CI runners), with a defined load: 200 redirects/s at a 90% cache-hit ratio over 1,000,000 links and 20 creations/s for 30 minutes. PR CI checks structure instead of milliseconds: redirects never touch the `main` pool or the coordinator (pool counters), and a relative regression check on a fixed benchmark.

---

## 9. Scaling to several API instances after year 1

Document in `docs/SCALING.md`. Already supported:
1. N `api` replicas behind the reverse proxy, unique `NODE_ID` each, exact proxy addresses in `KnownProxies` (§7.13).
2. Stateless JWT validation; Data Protection key ring in the database; cache invalidation across instances through `NOTIFY` (§7.3) already in place.
3. Namespace separation is arithmetic; alias ownership is the database unique index plus a per-alias advisory lock, both shared by all instances; the key fingerprint keeps keys identical.
4. Jobs use transaction-scoped advisory locks and `SKIP LOCKED`; creation limits are enforced in the handler; per-alias limits are database counts.
5. PgBouncer in transaction mode: no session state is used (`SET LOCAL` only). Configure **two PgBouncer pools** (separate database entries or users for `redirects` and `main`, each with its own pool size and reserve) so redirects keep their isolation; the `LISTEN` connection bypasses PgBouncer. Connection budget: N × (redirect pool + main pool) + coordinator + migrator + ops + listeners ≤ `max_connections` − reserved; set `max_connections` explicitly (§3).

Added at that time (new implementations of existing interfaces, no schema or protocol change):
6. Redis-backed rate-limiter partitions, plus a Redis container, when per-instance limits are no longer acceptable.
7. Multi-region: **one write primary**. Other regions run API instances that serve redirects from a local read replica and send every write to the primary; a regional coordinator leases from its own pool slice in the primary's `alloc` schema. **Read replicas are not used for redirects until a revocation-freshness design exists** (a lagging replica can return an active row for a link that was just disabled); until then every instance reads redirects from the primary. On a replica miss, the API checks the primary before caching a `404`. Separate write databases per region are not supported (alias ownership, quotas and idempotency need one database).

The coordinator stays a single instance; a second one would need a shared HWM store first.

---

## 10. Solution layout

```
src/Zaagl.Codes            Base62, permutation, GENERATOR_CAP, IsReserved, IsGeneratorReachable, KeyFingerprint
src/Zaagl.Contracts        Lease and status DTOs
src/Zaagl.Coordinator      Coordinator app, AllocDbContext + migrations, HWM store (IHwmFileSystem), allocator sink client, init/recover
src/Zaagl.Api              Web API, AppDbContext + migrations, Identity (+TOTP), plans, issuer, link state, purge, email (+webhooks),
                           analytics, freezes, abuse controls, account deletion/export, jobs, host routing, account pages
src/Zaagl.Ops              zaagl-ops CLI (init, declare-restore, recover, admin calls), packaged in the API image (with the `migrate` entrypoint)
BannedSymbols.txt          banned APIs (§2), enforced by the analyzer in every project
tests/Zaagl.Codes.Tests    (vectors.json, namespace tests)
tests/Zaagl.Coordinator.Tests     Testcontainers
tests/Zaagl.Api.Tests             WebApplicationFactory + Testcontainers (real roles) + fake clock + failpoints + Mailpit
tests/Zaagl.Architecture.Tests    NetArchTest rules (redirects use only the redirects pool; no coordinator call inside a transaction; no limiter as endpoint metadata)
tests/Zaagl.Scenarios             in-process API + coordinator with one fake clock (§8.2 suite a)
tests/Zaagl.Chaos                 container chaos: seeded load generator, acknowledged-code log, checker (§8.2 suite b)
tools/traceability-check          fails CI when a cited test is missing, skipped or has no assertion
bench/Zaagl.Codes.Bench
deploy/                    compose files, multi-stage Dockerfiles (non-root runtime), init-db scripts, secrets README
docs/                      DECISIONS.md, RUNBOOK.md, SCALING.md, API.md, TRACEABILITY.md, ABUSE.md (report handling: 12 h phishing/malware, 48 h other)
```

---

## 11. You **MUST NOT**
1. Return unused numbers, reissue a lease, or reuse a generated code.
2. Commit a lease before the HWM covers it, write a lower HWM, or initialise or recover automatically except the interrupted-init case (§6.4 row 2).
3. Run `new-length` without reading the allocator sink, start a new length before its marker is written, create a new installation id while one is known, or accept two differing fingerprints.
4. Enable code or alias creation before a successful key-fingerprint check, or change the recorded fingerprint.
5. Accept an alias that is generator-reachable, increase `GENERATOR_CAP`, or allow a pool range above it.
6. Accept an alias while a restore freeze is active.
7. Open an interactive transaction without `SET LOCAL lock_timeout`, reset it to the default inside the transaction, hold a transaction open while calling the coordinator, the reputation service, CAPTCHA or SMTP, or hold an advisory lock across several transactions.
8. Apply plan, time, quota, freeze or availability rules before the idempotency fast path, or quota/rate limits before the idempotency claim.
9. Let any row past `expires_at` redirect; cache a computed state instead of recomputing it.
10. Purge a row without holding its lock and rechecking `purge_after`; report a renewal before its commit; let a renewal move expiry backwards.
11. Treat a queued email as sent; send a warning without re-validating it; send any email to a disabled user (except a `security-notice`) or to a suppressed address; put a target URL or a token in an outbox row; make purge wait for an email.
12. Write click statistics without locking the batch's links first.
13. Rotate or add scramble keys; put the scramble key in an image, the repository, the database or a CI system; run `ops init` without escrow (`--key-escrowed`); generate a replacement key in a deploy script; mount the admin token into `api`; give the web app any secret of §3.
14. Change the alphabet, permutation, PRF encoding, key derivation, fingerprint formula or `GENERATOR_CAP` once codes exist.
15. Issue a generated code that matches a reserved word.
16. Use `System.Random` for anything security-relevant, or `DateTime.Now`/SQL `now()` where `TimeProvider` applies.
17. Check "does this code exist?" as a substitute for the insert. The insert with `ON CONFLICT` is the check.
18. Use `Database.SqlQuery`/`SqlQueryRaw` for `INSERT … RETURNING`.
19. Attach creation rate limiters as endpoint middleware.
20. Run migrations from the API or coordinator, or use `EnsureCreated`.
21. Make the API's `/health/ready` depend on the coordinator, a lease or the fingerprint check.
22. Build short URLs from the request `Host` header; serve API or account endpoints on the short host, or redirects on the API host; set a cookie on `zaa.gl`.
23. Let a redirect use the `main` data source, or wait on the coordinator, analytics, email or anything except one indexed lookup.
24. Add a cache entry without a byte size, or partition IPv6 rate limits by full address.
25. Reveal whether an email address has an account (`register`, `resend-confirmation` and `forgot-password` always `202`; account states only after a correct password).
26. Store any IP address or anything derived from one (except the keyed creator and prefix keys of §7.12 and a country), record raw clicks or daily statistics for guest links, or let any non-admin manage a guest link.
27. Treat a request with invalid or expired credentials as a guest.
28. Fetch a destination URL, ever (reputation checks are lookups only); create or edit a link without the blocklist check and a reputation lookup attempt (fail-open on timeout or error per §7.15); release an alias that is in `app.blocked_aliases` (except through `reinstate`); skip the lookup except when it times out, fails, or the monthly budget is exhausted (§7.15).
29. Trust `X-Forwarded-For` from anyone but the exact known proxies (staging only: also the access-gate ranges of §7.13), or let the BFF replace rather than append it.
30. Put a confirmation or reset token in a URL query string, or confirm an email on a `GET`.
31. Issue an Admin token without TOTP, or serve admin endpoints to addresses outside `Admin:AllowedNetworks`.
32. Write a migration that breaks the previous release, or create an index on `app.links` without `CONCURRENTLY`.
33. Ship a build that registers a non-no-op failpoint implementation or lets anyone set the clock at runtime.
34. Commit a lease that no **archive-confirmed** reservation covers (§6.4), or let `init` run on a non-empty allocator sink or mint an installation id during recovery.
35. Send a `NOTIFY` payload over 4,000 bytes, or keep serving from the cache while the invalidation listener is unhealthy.
36. Answer a redirect with `301`.
37. Re-enable a link taken down for abuse or safety through anything but the audited `reinstate`; check alias or destination blocks only outside the transaction that creates, edits or reinstates the link.
38. Describe click counts or statistics as exact.

---

## 12. Work plan, deliverables and checkpoints
Keep the build green after each step:
1. `Zaagl.Codes`: permutation, `GENERATOR_CAP`, reachability, reserved words, denylist normalisation, fingerprint, `vectors.json`, namespace tests, benchmark. Also: `BannedSymbols.txt`, the failpoint interface, the architecture-test project and the traceability checker, so every later step is held to them.
2. Coordinator: schema, resumable init with escrow flag and sink marker, startup table (one test per row), recovery modes (`new-length` from the sink), serialized leasing with marker writes, `IHwmFileSystem` with the crash-simulation test, failpoints, fingerprint check, status endpoint, `ops init`. Fixtures use the real roles from this step on.
   **Checkpoint 1: stop here and report** (tests, mutation score of `Zaagl.Codes` and the coordinator, traceability so far) for a human review before building the API on top.
3. API core: schema, roles, host routing, plans, guest creation (§7.12) with CAPTCHA and prefix quotas, URL rules, blocklist and reputation check, partition functions, link-state function (exhaustive tests), two data sources, lock-timeout policy, `ICodeIssuer` (sentinel, stress test, `Faulted`, fingerprint), link creation in the exact phase order of §7.2, redirect with byte-bounded cache, `NOTIFY` invalidation and status pages, renewal, client-IP chain (§7.13). Minimal compose and a chaos smoke (API `kill -9`, coordinator killed at failpoints, invariant checker).
   **Checkpoint 2: stop here and report** for a human review.
4. Accounts: Identity, registration with uniform responses and ToS, confirmation (fragment tokens, POST), password reset, JWT with `kid`, refresh-token families, logout-everywhere, login protection, admin TOTP and allowed networks, API keys, quotas and alias limits in the handler, user disable/enable with links, the generated authorization tests and the contracts of §7.9.
5. Analytics: channel, transactional batch writer (lock order of §7.5), bot counters, raw-event budget, stats, archive endpoints, partition jobs.
6. Jobs, email and abuse: purge, notifications, claim-based dispatcher, suppressions and webhooks, restore-freeze sync, re-checks, takedowns, reports, blocked aliases, account deletion and export, unconfirmed and tombstone clean-ups, `ops declare-restore`, `ops recover`.
7. Docker Compose (networks, fixed proxy addresses, limits, log rotation), init-db (roles, database, `btree_gist`), migrator entrypoint, `ops`, Mailpit (dev), healthchecks, secrets, the OpenAPI snapshot and breaking-change check, the N-1 migration job.
8. Scenario and chaos suites (§8.2) and the restore and host-loss procedures (§8.1).
9. Docs: README (one-command local run including `ops init`), DECISIONS (every accepted risk of Appendix J, alias reuse, freeze cost, key-leak and enumeration risk, plan-change effects on stats, at-least-once email, branded domains needing a migration, the open legal questions), RUNBOOK (init with escrow, restore with `declare-restore`, host loss, recovery modes, the quarterly drill, abuse handling within 48 h, backup and TLS boundary), ABUSE, SCALING, API.

**Traceability (`docs/TRACEABILITY.md`)**: a table mapping **every MUST NOT in §11, every guarantee in §8, every row of §8.2, every startup-table row of §6.4, every row of the authorization matrix (§7.4) and every URL rule (§7.6)** to the test(s) that prove it, with file and test name. Structural MUST NOTs are proven by the banned-API analyzer, architecture tests or configuration tests, and say so. **An item without a test is not done.** `tools/traceability-check` runs in CI and fails when a cited test does not exist, is skipped, or contains no assertion. Mutation testing (Stryker.NET) runs on `Zaagl.Codes`, the link-state function, the coordinator's lease and startup code and the issuer, with a score floor of 80%. Keep the table updated at each step and include it in every report.

At the end, report: what was built, test and chaos results, benchmark numbers, the traceability table, and every deviation from this prompt with the reason.

---

> Appendices A–G are revision history. Where they mention the alias journal, its endpoints, reconciliation or the rebuild token, those were **removed in v9** (Appendix H) and must not be built.

## Appendix A. Changes v1 → v2 (first review)
HWM written before commit under a serialized section; request-id re-check after the row lock; recovery guarantees separated; `SECURITY DEFINER` partition maintenance; raw Npgsql insert; API readiness independent of the coordinator; `itemDropped` callback; database authoritative for ranges; scaling wording; fresh-install definition; exact lease-consumption algorithm; admin validation; exclusion constraints; byte-exact crypto encoding; idempotency protocol; conflict classification; quotas; URL rules; cache invalidation; forwarded headers; bots; refresh-token families; authorization matrix; analytics transactions; roles; UTC; advisory locks; independent chaos log.

## Appendix B. Changes v2 → v3 (second review)
Reserved words apply to generated codes; independent key secrets; explicit initialisation; alias journal; DEFAULT-partition row move; idempotency before quota; key version stored with ranges and leases; range-update invariants; hardened partition functions; full API contracts; `Links:PublicBaseUrl`; restore procedure; byte-exact request hash; click counting for 301; month-aligned retention.

## Appendix C. Changes v3 → v4 (third review and product decisions)
Arithmetic separation of generated codes and aliases (generator cap + reachability rejection); database-ahead-of-HWM fails closed with `trust-database` recovery; resumable two-phase init and `reattach`; `new-length` recovery with operator evidence; lengths 5–10 seeded; alias reservations expire with per-user limits and compaction; alias claim after the idempotency claim; conflict classification from the actual row; issuer `Faulted` state; creation limits inside the handler; key rotation removed from Phase 0; link states; plans, expiry, renewal, quarantine, purge.

## Appendix D. Changes v4 → v5 (fourth review and product decisions)

| Item | Change | Section |
|---|---|---|
| C01: alias released before the old row is purged | A new claimant purges a `purgeable` predecessor inside its own transaction under a row lock; the coordinator releases only after a clock-skew margin. Test at release + 1 s. | §6.6, §7.2 |
| C02: alias claim id reused after the idempotency window | `claimId` is a fresh random UUID per attempt, never derived from the client key; the coordinator rejects a reused `claimId` with different data; the API verifies the response. | §6.1, §6.6, §7.2 |
| C03: compaction dropped data still needed | Explicit retention table: active ownership until release, claims (with original id and time) for 7 days, the rest archived 13 months. | §6.6 |
| C04: renewal racing purge | Renewal holds the row lock across the coordinator call and the update; purge uses `FOR UPDATE SKIP LOCKED` and rechecks; success only after commit. | §7.4, §7.8 |
| C05: validation before the stored-response lookup | Two phases: identify (parse, canonicalise, hash, fast path) before any plan, time, quota or availability rule. | §7.2 |
| G01: journal startup and corruption | Journal header bound to the installation; startup table J1–J5; only a partial final line counts as a crash; `journal-rebuild` recovery. | §6.6 |
| G02: missing journal reservation in reconciliation | `missing` handled as an integrity failure with a defined repair or disable. | §7.8 |
| G03: incomplete link states | Total, ordered state function including `purgeable`; state recomputed on every cache hit; "no redirect after expiry" invariant tested. | §4.3, §7.3 |
| G04: stats of purged links | Product decision: premium keeps daily stats forever via `link_archive`; free stats deleted with the link. | §1.1, §4.4, §7.8, §7.9 |
| G05: months vs days | Product decision: fixed days (30/90 free, 365/1,095 premium, 90-day quarantine). | §1.1 |
| G06: issuer before the first lease | `_current` starts as a sentinel empty lease. | §7.1 |
| G07: evidence for an unused length | Evidence must cover the whole installation life; ranked list; fallback to length 10. Off-host logs are now required by the runbook. | §6.4 |
| Product: quarantine for every link, email warnings before expiry and purge | One quarantine for generated and alias links; transactional outbox with per-user digests; purge waits for the warning (bounded). | §1.1, §4.3, §7.10 |
| Product: alias reuse after quarantine | Kept; documented as accepted risk. | §1.1 |
| Testability | `TimeProvider` everywhere, `@now` in SQL, Mailpit in dev and tests. | §2, §3 |

## Appendix E. Changes v5 → v6 (fifth review and product decision)

| Item | Change | Section |
|---|---|---|
| C01: failed alias creation extended the reservation | A claim never extends (case 3 returns the existing reservation unchanged). Creation checks the existing row under a lock before inserting, and calls `extend` only after a successful insert, only when re-creating an alias whose link was lost. | §6.6, §7.2 |
| C02: queued treated as sent | Separate `*_notice_queued_at` and `purge_notice_sent_at`; the dispatcher sets `purge_notice_sent_at` only on SMTP acceptance; the purge job uses it. | §4.3, §7.8, §7.10 |
| C03: alias reassignment bypassed the warning wait | Product decision: warnings are best effort; alias release never waits for email; only the scheduled purge waits (max 7 days). Stated consistently. | §1.1, §7.2, §7.8, §7.10 |
| C04: unsafe length-10 fallback | Removed. Without whole-life evidence the allocator stays blocked; everything else keeps working (`allocator-blocked` mode). | §6.1, §6.4 |
| C05: analytics racing purge | Analytics locks the batch's links (`FOR NO KEY UPDATE`, id order) before writing; missing links' events are dropped; interleaving test. | §7.5 |
| G01: "three emails a day" not enforced | Notification job runs once a day; unique `(user, stage, date)` outbox rows. | §4.4, §7.10 |
| G02: stale warnings after renewal | Outbox items snapshot `expires_at`; the dispatcher re-validates and drops stale items; outbox rendered at send time. | §4.4, §7.10 |
| G03: installation id during recovery | One resolution order for HWM, installation row and journal header; never a new id while one is known; explicit `rebindJournal`. | §6.4 |
| G04: partition functions and the fake clock | Both take `p_today` from `TimeProvider`, guarded to ±2 days of the database date unless a test-only setting allows it. | §4.4, §7.8 |
| G05: creation branches unclear | §7.2 restructured into phases A–D with explicit generated (9-G) and alias (9-A) branches and a common insert. | §7.2 |

## Appendix F. Changes v6 → v7 (sixth review)

| Item | Change | Section |
|---|---|---|
| C01: concurrent creation of the same new alias returned `500` | Per-alias transaction-scoped advisory lock (`pg_advisory_xact_lock`) serialises creators across instances; an insert conflict is always classified from the committed row (never assumed impossible); concurrency test. | §7.2 |
| C02: concurrent idempotent duplicates could get `429` | Quota and rate limit moved **after** the transactional idempotency claim; a `429` is never stored; concurrency test with one token left. | §7.2 |
| C03: same-owner claim retries not durable | Every claim, including same-owner, is a durable `claim` record with `requestedReservedUntil`, `resultReservedUntil`, `existing`; replay compares request fields and returns the recorded result; counted in the daily limit. | §6.1, §6.6 |
| C04: journal validation depended on the HWM | Journal identity checked against a reference id (HWM, else `alloc.installation`) independently of the allocator; `/health/ready` no longer requires both capabilities; new `/health/capabilities`. | §6.1, §6.6 |
| G01: stale warning after renewal can still slip through | Documented as accepted best effort; no lock across SMTP. | §7.10 |
| G02: admin credential exposure | API holds only `COORDINATOR_TOKEN`. Admin and rebuild tokens live only in a one-off `ops` container; new rebuild-only token; API journal-rebuild endpoint removed. | §3, §6.1, §6.6 |
| G03: common insert looked nested | §7.2 rewritten with phase headings; steps 6–13 all top level; only step 9 branches (G, A). | §7.2 |

## Appendix G. Changes v7 → v8 (seventh review) — specification frozen

| Item | Change | Section |
|---|---|---|
| C01: renewal could shorten expiry while the journal kept the longer reservation | Renewal is forward-only (`400 renewal-must-extend`); shortening is not supported in Phase 0. | §7.4 |
| C02: nothing stopped a different scramble key from being deployed | Permanent key fingerprint recorded at init in the HWM, journal header and `alloc.installation`; checked by the coordinator on every lease, claim and extend; the API refuses to create codes or accept aliases without a matching check; preserved through recovery. | §5.4, §6.1, §6.4, §6.6 |
| C03: reconciliation outcomes incomplete | Exhaustive outcome handling; `reconciliation_issues` table; transient errors retried with the link kept active; permanent conflicts disable the link pending admin action; quota-free, capped `repair` endpoint; admin endpoints to list and resolve issues. | §7.8, §7.9 |
| M01: notification job frequency | §7.8 now says daily. | §7.8 |
| M02: "not-ready" wording | Uninitialised state reported via capabilities; readiness stays healthy. | §6.4 |
| M03: two-owner test expectations | Winning owner's duplicates expect `alias-already-yours`; others `alias-unavailable`. | §7.2 |
| Advisory-lock wait unbounded | `lock_timeout = 3s` around the alias lock; `503 alias-busy`. | §7.2 |

## Appendix H. Changes v8 → v9 (eighth review and product decisions)

| Item | Change | Section |
|---|---|---|
| Alias journal judged too costly for the rare case it covers | **Removed** (product decision): no journal file, claim/extend/verify/repair endpoints, rebuild mode or token, reconciliation job or issue tracking. Alias ownership is the database unique index plus a per-alias advisory lock. The coordinator only leases. Replaced by a 30-day **alias restore freeze** (automatic detection plus a mandatory operator `declare-restore`). Namespace separation (§4.5) is unchanged. Earlier appendices describe the journal as history only. | §1.1, §6, §7.2, §7.11, §8 |
| Email never confirmed | Confirmation email at registration; unconfirmed accounts keep full access but receive no other email (product decision); password reset for confirmed addresses; per-address send limits; purge does not wait for warnings that are never sent. | §1.1, §4.4, §7.4, §7.8, §7.10 |
| Disabled users' links kept working | Admin chooses account-only or account-and-links; links disabled with reason `user-disabled`; enabling restores only those; no emails to disabled users. | §4.3, §4.4, §7.4, §7.9 |
| Slow coordinator could exhaust the pool and stall redirects | Separate `redirects` data source; creation concurrency limiter; `issuer.Take()` before the transaction; no external call inside any transaction. | §7.0, §7.2 |
| Off-host HWM copy | Not added (product decision); stated as an accepted risk. | §1, §6.4, §8 |
| Unbounded memory cache | `SizeLimit` and `Size = 1` on every entry. | §7.3 |
| IPv6 rate-limit evasion | Every IP limiter partitions IPv6 by /64. | §7.3, §7.4 |
| Data Protection keys and `btree_gist` | `app.data_protection_keys`; `init-db` creates `btree_gist`. | §3, §4.1, §4.4 |
| Proving the MUST NOTs | `docs/TRACEABILITY.md` maps every MUST NOT, guarantee, chaos row and startup row to a test; untested means not done. Mandatory checkpoint after the coordinator (step 2). | §12 |

## Appendix I. Changes v9 → v9.1 (product decision: guest links)

| Change | Section |
|---|---|
| Guests can create generated links and aliases: 20 per 24 h per IP partition, 7-day expiry (default = max), no quarantine, no renewal/edit/delete, no API keys, no statistics, no emails. | §1.1, §7.12 |
| `links.owner_id` nullable; `guest_creator_key` (HMAC of the IP partition with new secret `GUEST_KEY`); `purge_after = expires_at` for guests; idempotency keyed by `scope`. | §3, §4.3, §4.4 |
| Creator identification: no credentials = guest; invalid credentials = `401`. | §7.2 |
| Guest clicks not recorded; purge does not wait for guests; guest aliases free at expiry. | §7.3, §7.8 |
| Abuse controls: per-IP token bucket, global hourly ceiling, kill switch, optional CAPTCHA hook (off by default). | §7.12 |

## Appendix J. Changes v9.1 → v9.2 (seven-discipline review, second external review, product decisions)

**Product decisions taken for v9.2**

| Decision | Section |
|---|---|
| Hosts: short links on `zaa.gl`, API on `api.zaa.gl`, web app on `app.zaa.gl`, email from `notify.zaa.gl`, staging on `staging.zaa.gl` and its subdomains; a spare domain parked for emergencies. | §1.2 |
| Abuse controls ship at launch: reputation check (Google Web Risk) with re-checks, destination blocklist, CAPTCHA (Turnstile) on guests and account flows, takedown tools with permanently blocked aliases and kept evidence, report channel. TechTroll team responds within 48 hours. | §1.1, §7.15 |
| Unconfirmed accounts cannot sign in; they expire after 7 days; registering again restarts the registration. | §1.1, §7.4 |
| Purge always happens on time; nothing waits for email. | §1.1, §7.8, §7.10 |
| Operator TechTroll (Egypt); production hosted in the EU. | §1 |
| Self-service account deletion and data export at launch. | §7.14 |
| One API replica in year one (a few seconds of redirect downtime per deploy accepted). | §1, §3 |
| Files, images and snippets later under short prefixes on `zaa.gl`. | §4.5 |

**Product decisions re-confirmed against review advice** (recorded as accepted risks in `docs/DECISIONS.md`)

| Kept | Reviewers' concern |
|---|---|
| Aliases are released to anyone after the 90-day quarantine | Old shared links can lead to a stranger's destination (trust and phishing). |
| Guests may create aliases, released the moment the 7-day link expires | Zero-day hold makes guest aliases easy to take over. |
| Free links: 30 days default, 90 maximum | Shared free links will be dead within months. |
| Premium as specified (admin-assigned; longer expiry and kept statistics) | Little reason to pay; no purchase path at launch. |
| Subdomains of `zaa.gl` instead of a second domain | A domain-level blocklisting of `zaa.gl` also affects the API, app and email hosts; staging is same-site with production (mitigated by host-only cookies, CSRF tokens and the access gate). |
| No legal retention log of creator IPs | Applicability of Egyptian Law 175/2018 follows the operator, not the hosting location (open question below). |
| No schema preparation for branded domains | Adding them later needs a migration of `app.links` uniqueness. |

**Open questions for counsel** (before launch): Egyptian Law 175/2018 traffic-data retention for an Egyptian operator hosting in the EU; Egyptian PDPL 151/2020 licensing and permission for the cross-border transfer to the EU; whether a GDPR Art. 27 representative is needed; whether the EU DSA hosting-service duties apply (certainly once files arrive); terms of service, acceptable use policy and privacy notice.

**Technical changes**

| Finding | Change | Section |
|---|---|---|
| Host loss stopped generated links for good (whole-life evidence) | Off-host allocator sink with write-once length markers; `new-length` computes the next safe length from the sink and the restored database; no logs needed; host-loss runbook and quarterly drill. | §6.2, §6.4, §8.1, R8 |
| Scramble key could not be backed up | Key escrow mandatory; `ops init --key-escrowed`; key treated as permanent data; per-length rotation path documented for later. | §3, §5.3, R9, §11 #13 |
| Codes described as unpredictable | Reworded: not guessable by counting, not secret; tiered miss limits, global breaker, enumeration alert. | §1, §5.3, §7.3 |
| Client IP broken behind proxy and BFF; spoofable | Per-environment `ForwardLimit`, exact `KnownProxies`, BFF appends, `edge`/`backend` networks, API port not published, spoof tests. | §3, §7.13 |
| Browser session handling unspecified | BFF holds tokens in `__Host-` cookies, CSRF double-submit, no CORS, no secrets in the web app. | §7.13 |
| Cache could serve disabled links on other instances | `NOTIFY` invalidation delivered on commit, from day one; byte-bounded cache. | §7.3, R5 |
| Account enumeration and address squatting | Uniform `202` on register, resend and forgot-password; account states only after a correct password; unconfirmed TTL. | §7.4, §11 #25 |
| API keys survived password reset; admin password-only; lockout abuse | Keys revoked on reset and logout-everywhere; key-creation notice; admin TOTP and allowed networks; per-(account, partition) lockout with progressive delay. | §7.4 |
| Data Protection keys readable from backups | Key ring encrypted with `DATA_PROTECTION_CERT`; tokens generated at send time; JWT `kid` with a key set. | §3, §4.4, §7.4, §7.10 |
| Tokens in query strings; email pages without headers | Fragment tokens posted by the page; strict CSP and headers; confirm by POST. | §7.4 |
| Deploy restarted the coordinator every time | Coordinator image tagged by input hash. | §3 |
| Disk and memory exhaustion | No bot rows, raw-event budget, log rotation, memory limits, separate `pgdata` volume. | §3, §4.4, §7.5 |
| Click data more identifying than needed | No IP-derived data stored; optional country only; guest links keep an aggregate counter for abuse detection. | §4.4, §7.3, §7.5 |
| Partition DDL and click flush lock chain | Lock timeouts on partition functions, hourly retry, flush takes table locks first. | §4.4, §7.5 |
| Job advisory locks across many transactions | Single-transaction jobs only; claim-based dispatcher (`sending`, `locked_until`); short purge batches. | §7.8, §7.10 |
| Unbounded lock waits | `lock_timeout` once per interactive transaction, never reset; lock order and chunking for bulk writes. | §7.0, §7.2 |
| HOT updates and bloat on `app.links` | Fillfactor 80 and autovacuum settings in the first migration. | §4.3 |
| Migrations could stall redirects | Migrator lock timeout, concurrent index builds, redirect command timeouts, expand-only rule with an N-1 job. | §3, §7.0 |
| PgBouncer would merge pools | Two PgBouncer pools; `No Reset On Close`; LISTEN outside PgBouncer; connection budget. | §7.0, §9 |
| EF Core traps | `HasAlternateKey` for `uq_links_code`; `ExcludeFromMigrations` for `click_events`; default privilege revoke for functions. | §4.1, §4.3, §4.4 |
| Multi-region claim was wrong | One write primary; regional read replicas; regional pools. | §1, §4.6, §9 |
| Tests could not prove races, crashes or time rules | Failpoints, clock policy, in-process scenarios, crash-simulated HWM file system, failing invariants, seeded chaos, perf profile, banned-API analyzer, architecture tests, traceability checker, mutation testing, real-role fixtures, second checkpoint. | §2, §4.1, §8.2, §8.3, §10, §12 |
| `guest_creator_key` missing from the insert column list | Added. | §7.2 |
| Independent consistency pass on the v9.2 draft | 25 internal fixes: deletion keeps `purge_after ≥ expires_at` and locks the account first; health endpoints exempt from host routing; egress network for the allocator sink; `disabledKind` in the link resource; markers on admin advance and interrupted init; re-check claiming outside transactions; reputation check before PATCH locks; admin TOTP enrolment endpoints; CAPTCHA token reuse under idempotency; and smaller wording fixes. | §1.2, §3, §6.1, §6.4, §7.2, §7.4, §7.5, §7.9, §7.10, §7.14, §7.15, §11 |

## Appendix K. Changes v9.2 → v9.3 (product decision: API keys)

| Change | Section |
|---|---|
| API keys are premium only: free users get `403 plan-required` when creating one; `/api/v1/me` reports `limits.apiKeys`. | §1.1, §7.4, §7.9 |
| Key authentication checks the owner's plan; a downgrade to free revokes all keys in the plan-change transaction and sends a `security-notice`; an upgrade does not restore them. | §7.4, §7.9 |
| New acceptance row for key creation on the free plan and for downgrade. | §8.2 |

## Appendix L. Changes v9.3 → v9.4 (third external review and product decisions)

**Product decisions**

| Decision | Section |
|---|---|
| After losing the host, the allocator **resumes where it stopped**: reservations live in a database table and only archive-confirmed reservations are used, so a restore to the end of the WAL archive brings back every reservation that numbers were issued under. Length markers remain the fallback. | §3, §6.2, §6.4, §8 |
| Web Risk outages and an exhausted budget: links are still created for everyone (fail-open) and re-checked later. | §7.15 |
| Phishing and malware reports handled within 12 hours, with an alert per report and an emergency disable; other reports within 48 hours. | §7.15 |
| Web Risk budget: 100,000 lookups a month (free tier), create and edit first, re-checks by recent clicks. | §7.15 |

**Technical changes**

| Finding | Change | Section |
|---|---|---|
| C01: bulk `NOTIFY` over PostgreSQL's 8,000-byte limit | Payloads ≤ 4,000 bytes, several per chunk; test with 1,000 aliases of 64 characters. | §7.3 |
| C02: block checks raced with creation | Alias blocks re-checked under the alias lock (blocking takes the same lock); destination fence with a shared/exclusive advisory lock and a takedown sweep after the block commits. | §7.2, §7.15 |
| C03: "exact" counters with a lossy channel | Analytics declared best effort with loss metrics. | §7.5 |
| C04: a pre-initialisation restore could mint a new installation | Sink keyed by pool with a write-once manifest; `init` needs an empty sink; recovery adopts the manifest identity; startup row 1a. | §6.4 |
| H01: `301` cannot be withdrawn | `302` only. | §7.3 |
| H02: `NOTIFY` is not durable | Listener heartbeat; cache bypass while unhealthy; bounded exposure in R5. | §7.3, R5 |
| H03, H10: takedowns could be undone; denylist too broad | `409 takedown-active` on ordinary enable; audited `reinstate`; broad words exact-match only. | §4.5, §7.15 |
| H04: `new-length` wastes a whole length | `resume` from archive-confirmed reservations; `new-length` is a last resort with a remaining-lengths alert. | §6.4 |
| H07: Web Risk cost | Monthly budget, priorities, provider cache durations, alerts. | §7.15 |
| H08: guest quotas could be overshot | Guest limits claimed under row locks on hourly buckets (hard); account limits stay approximate. | §7.12 |
| H09: parallel refreshes revoked sessions; web session key missing | `409 refresh-raced` within 30 s; web-only session keys; single-flight refresh. | §7.4, §7.13 |
| H11: retention after deletion undisclosed | Retained data and reasons listed; notice address cleared after sending. | §7.14 |
| H12: Turnstile bindings | Hostname, action, token age and retry idempotency key checked; keys per environment. | §7.12 |
| H13: stale read replicas | No replicas for redirects until a revocation-freshness design exists. | §9 |
| H05, H06 (partly), H14 | Already product decisions (domains, fail-open, plan terms); unchanged and recorded. | Appendix J |
| Independent check of the v9.4 draft (allocator) | `resume` verifies the WAL restore itself (timeline history and archive listing) and has strict preconditions; every position change writes a reservation first; renewal handles length changes; confirmation ignores non-segment names and waits for earlier `.ready` segments (`pg_monitor`); drills use separate archive and sink; explicit `recovery_target_timeline`; identity rule reconciled with `reattach`; interrupted init and unreachable sink rows; PATCH and reinstate use the destination fence; reinstate respects disabled users and notifies the cache; shared monthly lookup budget row; length-tagged guest keys; stale references fixed. | §6.4, §6.5, §7.0, §7.3, §7.4, §7.12, §7.15, §8, §10, §11 |
