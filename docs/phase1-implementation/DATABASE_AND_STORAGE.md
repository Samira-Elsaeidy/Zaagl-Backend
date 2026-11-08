# Phase 1 database and storage

| | |
|---|---|
| Owner | zaagl-backend (schemas, jobs), zaagl-devops (volumes, filesystem) |
| Status | PLANNING, awaiting approval. No Phase 1 code exists yet. |
| Source | _drafts/backend.md §1, §2, §4–7 and _drafts/devops.md §2, written 2026-11-09 by the planning agent named above and kept verbatim below this header |
| Decisions | Numbered PD-nn in [DECISIONS.md](DECISIONS.md). Draft question ids (D-n, Qn, Q-nn) map to PD entries there. |

## Consolidation notes

These are the lead architect's overrides. Where they differ from the draft text below, they win.

- **Schemas:** one per module (`resources`, `files`, `media`, `snippets`), plus expand-only changes to `links` (`target_kind`, `resource_id`) and `analytics` (event kind). No migration is created during planning.
- **Index rule:** indexes on `links.links` are created `CONCURRENTLY` in non-transactional migrations (spec §3, MUST NOT #32).
- **Retention table:** values are proposals until PD-04 and PD-06 are approved.
- **Burn (PD-05):** the definition in Part B is the consolidated proposal.
- **Storage backend (PD-15):** local disk behind `IFileStorage` v2. The `objects/aa/bb/<uuid>` shard uses a hash of the id, not its time-ordered prefix.
- **Test databases:** each test class gets its own database cloned from a migrated template (QA-002). Tests stop using the shared `localhost:5433` dev database.

---

## Part A: backend audit and data model

## 1. Audit findings

### 1.1 Reusable as-is (or with expand-only changes)
| Piece | Where | Reuse in Phase 1 |
|---|---|---|
| Module pattern: `Configure()` + `Add<X>Module()` + per-schema `__ef_migrations` + `IModuleMigrator` | `Modules/Links/LinksModule.cs:17-20,22-128`; `Modules/Analytics/AnalyticsModule.cs:14-37`; `SharedKernel/Persistence/ModuleMigrator.cs` | Copy for Resources/Snippets/Files/Media (schemas `resources`, `snippets`, `files`, `media`). |
| Module stubs already exist (empty `Domain/Application/Infrastructure/Contracts` + `.gitkeep`), referenced by Api and Worker | `src/Modules/{Resources,Snippets,Files,Media}`; `Zaagl.Api.csproj`, `Zaagl.Worker.csproj` | No new projects needed. `FilesEndpoints.MapFiles` etc. are empty stubs (`Zaagl.Api/Endpoints/FilesEndpoints.cs:3-6`) and are **not mapped** in `Program.cs:259-269`. |
| Code issuing (lease + Feistel + reserved skip), alias claim (advisory lock, blocked/denylist, purgeable reclaim) | `Links/Application/LinkService.cs:114-204` | Must be reused for content short links (rule: never redesign). Only reachable through `LinkService.CreateAsync(LinkInput)` today. |
| Five-state link calculator | `Links/Contracts/LinkContracts.cs:181-200` | Same shape for a `ResourceStateCalculator` (computed, never stored). |
| AccessGate (password/expired/purgeable) + atomic click consume | `Access/Application/AccessGate.cs:21-44`; `Links/Application/LinkResolver.cs:27-34` | Password + expiry gates for content; the conditional-UPDATE pattern is the model for the burn claim. |
| Idempotency claim (`INSERT … ON CONFLICT DO NOTHING`, request hash, replay) | `LinkService.cs:444-474`; table `links.idempotency_keys` (`Links/Domain/Link.cs:224-233`) | Same algorithm, but a per-module table (no cross-schema writes). |
| SKIP LOCKED batch jobs on `PeriodicTimer` | `Links/Application/PurgeService.cs:26-124`; `AbuseControls.cs:131-200` (claim short, external calls outside tx) | Pattern for expiry, burn-finalize, scan, derivative, reconciliation jobs. |
| Takedown routine, evidence, reports by code | `AbuseControls.cs:93-124`, `ReportService` `:204-250` | Extend to resources (content takedown + hash blocklist). |
| Guest identity/quota (HMAC IP partition, transactional quota) | `Links/Application/GuestCreation.cs:44-130`; `LinksEndpoints.cs:52-78` | If guests may post snippets. |
| Plan provider; tier stored as int | `Billing/Application/BillingService.cs:28-37`; `Billing/Infrastructure/BillingDbContext.cs:22` | Adding `PlanTier.Pro = 2` needs **no** Billing migration. |
| API keys (hashed, prefix `zgl_`, scopes array) | `Identity/Application/IdentityService.cs:35-39,392-416,556-567`; `Zaagl.Api/Infrastructure/ApiKeyAuthentication.cs:22-35` | CLI/desktop auth — after scope enforcement is fixed (R1). |
| Result/Error → ProblemDetails | `SharedKernel/Result.cs:3-12`; `Zaagl.Api/Infrastructure/ApiResults.cs:8-27` | Extend with new codes + `type` URIs (R2). |
| Integration-test convention: real Postgres `localhost:5433`, no Testcontainers | `tests/Zaagl.Links.Tests/PurgeJobTests.cs:15-36` | Same for new tests; `tests/Zaagl.Files.Tests` and `Zaagl.Snippets.Tests` exist but are empty (xunit, module ref only). |

### 1.2 Missing
- **IFileStorage has no implementation and no consumer** (`SharedKernel/Storage/IFileStorage.cs:3-8`; grep finds no `: IFileStorage`). No `LocalFileStorage`, no storage root config, no areas (temp/quarantine/objects/thumbnails/previews).
- **No upload stack**: no tusdotnet, nClam, NetVips, FFmpeg wrapper, MimeDetective, Markdig, HtmlSanitizer, DiffPlex package references anywhere (grep over `*.csproj`/`*.cs`).
- **Wolverine is not used**: no package reference; background work is plain `BackgroundService` registered inside module extension methods (`LinksModule.cs:98,106-116`, `IdentityModule.cs:40-46`, `AnalyticsModule.cs:32-33`).
- **Zaagl.Worker is an unused template**: logs every second (`Zaagl.Worker/Worker.cs:3-16`, uses banned `DateTimeOffset.Now` at :11), registers no module (`Program.cs:1-7`) despite referencing all of them.
- **No Pro tier**: `PlanTier { Free, Starter }` (`Billing/Contracts/BillingContracts.cs:3-7`); `PlanLimits` has no size/retention/storage fields (`:16-29`); one Stripe price (`BillingService.cs` `StripeOptions.StarterPriceId`).
- **No content host**: `HostRoutingOptions` has only `Short` and `Api` (`Zaagl.Api/Infrastructure/HostRouting.cs:3-12`); every non-`/api` path is "redirect-shaped" (`:57-61`), so a files/usercontent host cannot be added without changing the decision function.
- **No Kestrel/body-size configuration**: no `MaxRequestBodySize`, `FormOptions`, `MinRequestBodyDataRate` or request-timeout settings in `Program.cs` or `appsettings.json` → Kestrel default 30,000,000 bytes per request.
- **Link creation only accepts URLs**: `LinkInput.DestinationUrl` required (`LinkContracts.cs:11-22`), `Normalize` rejects non-http(s) (`LinkService.cs:909-953`), reputation lookup always runs (`:881-889`), `destination_url` NOT NULL (`LinksDbContext.cs:35`), `LinkSnapshot` has no kind/resource id (`LinkContracts.cs:203-221`) and the redirect SQL reads only link columns (`LinkResolver.cs:72-80`).
- **Account deletion/export only cover links** (`IdentityService.cs:442,476-481` via `IAccountDataEraser`/`ILinkExportSource`).

### 1.3 Risks (ordered)
| # | Risk | Evidence | Mitigation (task) |
|---|---|---|---|
| R1 | **API-key scopes are never enforced.** Claims are added but no endpoint or policy checks `scope`; a `links:read` key can create/delete links. Adding `files:write` keys without fixing this is a privilege bug. | `ApiKeyAuthentication.cs:31-32`; `Program.cs:174-186` (only Interactive/Admin policies); grep `RequireClaim`/`"scope"` in Endpoints = 0 | BE-001 |
| R2 | Problem responses have no stable `type` (only `code` extension), contrary to spec §7.9. CLI/desktop need machine-readable errors. | `ApiResults.cs:8-27` | BE-002 |
| R3 | Tier comparisons use `== PlanTier.Starter`; a new Pro tier would silently lose stat archiving and API keys. | `PurgeService.cs:102`; `IdentityService.cs:395`; `BillingService.cs:58,149,164` | BE-003 (capability flags) |
| R4 | Kestrel 30 MB default body; Cloudflare Free 100 MB per proxied request (tech stack §09); a 5 GB upload must be chunked and go to a DNS-only host. `LinksEndpoints.cs:103-112` buffers `IFormFile` — do not copy that pattern. | Program.cs (absent) | BE-005, BE-011 |
| R5 | `IFileStorage` shape is unfit: `SaveAsync` picks the key (no temp→objects promote, no area), returns no size/hash, `OpenReadAsync` has no offset/length (ranges), no stat/list (reconciliation), no internal path for X-Accel-Redirect, `contentType` is a client value. | `IFileStorage.cs:5-7` | BE-004 (replace — zero consumers, safe) |
| R6 | Layout `objects/aa/bb/<uuid>` with UUIDv7 (tech stack example `019b7f42… → objects/01/9b`) shards by **timestamp**: months of uploads land in one directory. | tech stack §08 | Shard on random bits (e.g. `SHA-256(key)[0..2]` or UUIDv4 key). BE-004 |
| R7 | ClamAV limits: clamd `StreamMaxLength` default 25 MB, INSTREAM/MaxScanSize/MaxFileSize hard-capped below 4 GB → a 5 GB file cannot be fully scanned. ZIP bombs/encrypted archives. | ClamAV docs | Scan by path on a shared volume, explicit "too-large-to-scan" policy (Q8). BE-014 |
| R8 | Content must never execute in `zaa.gl` origin and `zaa.gl` never sets cookies (spec §1.2, §4.5 r6). SVG/HTML/PDF from uploads are XSS vectors. | spec | Separate registrable content domain, `CSP: sandbox`, `nosniff`, attachment by default. BE-005, BE-016 |
| R9 | `ForwardedHeaders` trusts any proxy (`KnownIPNetworks/KnownProxies.Clear()`), so per-IP limits on a DNS-only files host (no Cloudflare in front) are spoofable via `X-Forwarded-For`. Spec MUST NOT #29. | `Program.cs:47-53` | Per-host forwarded-header config; files host uses direct peer IP from Nginx. BE-005 |
| R10 | Bot classifier marks `curl/`, `wget/`, `python-requests`, `go-http-client` as bots — the CLI's own traffic. Bots skip click limits (`ResolutionService.cs:101`). Using UA to exempt preview bots from burn would let any curl download bypass burn. | `Resolution/Application/ClientClassifier.cs:26-29` | Burn never depends on UA (§5). |
| R11 | Background jobs registered in `Add*Module` would all start in the Worker too if it reuses module registration (CreationGatePoller, LISTEN listener, purge…). CPU-heavy scan/ffmpeg in the API process threatens redirect latency (tech stack "shared compute" risk). | `LinksModule.cs:54,98,106-116` | Split `AddXModule` / `AddXJobs`. BE-013 |
| R12 | Existing jobs use banned `Database.SqlQuery` (warning-only) and entities default `DateTime.UtcNow`. New code must use raw Npgsql + `TimeProvider`. | `PurgeService.cs:74`; `AbuseControls.cs:~162`; `Link.cs:63-64`; `LinkService.cs:98,109`; `BannedSymbols.txt` | Review gate on every BE task. |
| R13 | "Pro: permanent storage" contradicts spec §1.1 "every link expires" (implementation already allows null expiry: `LinkService.cs:476-481`). 90-day quarantine would keep 5 GB blobs for 90 days after expiry. | spec §1.1, PRD pricing | Q1, Q2 |
| R14 | Single in-memory fixed-window rate limiter per process; fine for one VPS, not for >1 instance. | `Program.cs:69-86` | Accept for Phase 1; quotas live in DB transactions (spec §7.2 step 9). |
| R15 | Redirect regex accepts 3-char segments (`{2,63}`), spec §7.3/§4.5 r6 says 1–3 chars → 404 before I/O; `/s/…`, `/f/…`, `/i/…` are multi-segment so they don't collide, but LO-B relies on this namespace. | `ResolutionEndpoints.cs:16` | BE-007 |

---

## 2. Domain model

### 2.1 Link-to-content resolution — options for the architect
| | LO-A: link targets a resource | LO-B: resource-owned paths | LO-C: hybrid (recommended for backend) |
|---|---|---|---|
| Public URL | `zaa.gl/{code}` (generated or alias) | `zaa.gl/s/{id}`, `/f/{id}`, `/i/{id}` (spec §4.5 r6) | `zaa.gl/{code}` for sharing; content host addresses by opaque `public_id`/tickets |
| Links schema | expand: `links.target_kind smallint default 0` (url/snippet/file/media), `links.resource_id uuid null`; `destination_url` keeps a canonical viewer URL (no NOT NULL change) | none | as LO-A |
| Reuse | code issuing, aliases, password/expiry/click gates, analytics, takedown, purge | none of the code issuer (own id generator), own gates/analytics | full |
| New contract | `ILinkIssuer.CreateForResourceAsync(owner, resourceId, kind, alias?, expiresAt, policy)` in Links, skipping URL normalize/reputation/blocklist but keeping Phase A–D, lease, alias lock, idempotency | — | as LO-A |
| Redirect | `ResolutionService` sees `kind≠url` → 302 to `app.zaa.gl/v/{code}` (viewer) | Next.js route serves viewer directly | as LO-A |
| Open issue | Direct visits to the viewer bypass `zaa.gl` click-limit consumption → redirect must hand over a short signed pass (URL fragment), or the descriptor endpoint consumes clicks itself | — | same |
Everything below is written so `resources.resources` carries both `public_id` (always) and `link_id` (null under LO-B).

### 2.2 Module ownership
- **Resources** (`resources` schema): the shared envelope — ownership, lifecycle, gates specific to content (burn), link binding, quotas, download grants, idempotency. Exposes `IResourceDirectory`, `IResourceLifecycle`, `IResourceDataEraser`, `IStorageQuota` in Contracts.
- **Files** (`files` schema): bytes — upload sessions, storage objects, scan state, storage-deletion outbox, content-hash blocklist, file items.
- **Media** (`media` schema): media metadata + derivatives (thumbnails/posters/previews/SVG-safe views).
- **Snippets** (`snippets` schema): text + immutable versions.
Cross-module calls only through Contracts; every multi-row write stays in one schema/one DbContext (burn claim + grant both live in `resources`).

### 2.3 Entities
**resources.resources** — `id uuid v7 pk`, `public_id char(22)` (base64url of 16 CSPRNG bytes, unique; never derived from code), `kind smallint` (1 snippet, 2 file, 3 media), `owner_id uuid null`, `guest_creator_key bytea null` (check owner xor guest, as `LinksDbContext.cs:44-45`), `link_id uuid null` (no FK, other schema), `title varchar(200) null`, `size_bytes bigint not null default 0`, `processing_status smallint` (0 pending, 1 scanning, 2 ready, 3 rejected, 4 error), `rejected_reason varchar(50) null` (`infected`, `type-not-allowed`, `too-large`, `blocked-hash`, `scan-unavailable`), `password_hash varchar(100) null` (LO-B only; LO-A uses `links.access_policies`), `expires_at timestamptz null`, `purge_after timestamptz null`, `bytes_delete_after timestamptz null` (Q2), `burn_after_download bool not null default false`, `burn_claimed_at timestamptz null`, `burn_grant_id uuid null`, `burned_at timestamptz null`, `download_count bigint not null default 0`, `deleted_at`, `disabled_at`, `disabled_kind` (`admin|user-disabled|abuse|safety`, check paired with `disabled_at`), `disabled_reason`, `created_via smallint` (web/cli/desktop/api), `created_at`, `updated_at`. Indexes: `(owner_id, created_at desc, id desc)`, `(purge_after)`, `(expires_at)`, `(burn_claimed_at) where burned_at is null and burn_claimed_at is not null`, unique `(public_id)`, `(link_id)`.
**Computed state** (top to bottom, first match, mirrors spec §4.3): `purgeable` (now ≥ purge_after) → `deleted` → `disabled` → `burned` (burned_at or claim grace elapsed) → `expired` (now ≥ expires_at) → `processing`/`rejected` (processing_status) → `ready`.
**resources.download_grants** — `id uuid pk`, `resource_id uuid`, `kind smallint` (owner, viewer, burn), `disposition smallint` (attachment, inline-preview), `derivative varchar(30) null`, `created_at`, `expires_at`, `max_requests int`, `requests_used int`, `first_request_at null`. Index `(resource_id)`, `(expires_at)`.
**resources.storage_usage** — `owner_id uuid pk`, `used_bytes bigint`, `reserved_bytes bigint`, `updated_at`; changed only inside transactions holding `pg_advisory_xact_lock(hashtextextended('zaagl.quota:'||owner_id,0))`.
**resources.idempotency_keys** — same shape as `links.idempotency_keys` (`Link.cs:224-233`), scope `u:`/`g:`.
**files.upload_sessions** — `id uuid pk` (= tus file id), `owner_id`/`guest_creator_key`, `declared_length bigint`, `received_length bigint`, `declared_name varchar(255)` (untrusted, display only), `declared_type varchar(100)` (ignored for decisions), `intended_kind smallint`, `plan_tier_at_create smallint`, `max_bytes bigint`, `reserved_bytes bigint`, `temp_key varchar(100)`, `status smallint` (open, complete, finalized, aborted, expired), `idempotency_key null`, `created_at`, `last_activity_at`, `expires_at` (24 h idle). Index `(status, expires_at)`.
**files.storage_objects** — `id uuid pk`, `storage_key varchar(100) unique` (e.g. `objects/3f/a2/<uuid>`), `area smallint` (temp, quarantine, objects), `size_bytes bigint`, `sha256 bytea(32)`, `detected_mime varchar(100)`, `detected_ext varchar(20)`, `scan_status smallint` (pending, clean, infected, error, skipped-too-large), `scan_signature varchar(200) null`, `scan_engine varchar(100) null` (clamd + db version), `scan_attempts int`, `scanned_at null`, `created_at`. Index `(scan_status, created_at)`, `(sha256)`. One object per upload (no dedup across owners in Phase 1: avoids cross-tenant existence oracles and ref-counted deletes).
**files.file_items** — `resource_id uuid pk`, `storage_object_id uuid`, `original_name varchar(255)` (sanitized, §4 step 6), `content_type varchar(100)` (detected), `size_bytes`.
**files.storage_deletions** (outbox) — `id bigint identity`, `storage_key`, `reason varchar(30)`, `not_before timestamptz`, `attempts int`, `last_error null`. Written in the same tx as the state change; a job deletes bytes (crash-safe, idempotent).
**files.processing_tasks** — `id bigint identity`, `kind varchar(30)` (scan, finalize), `subject_id uuid`, `status` (pending, running, done, failed), `run_after`, `locked_until`, `attempts`, `last_error`. Claimed `FOR UPDATE SKIP LOCKED` (same as `media.processing_tasks`).
**files.blocked_content_hashes** — `sha256 bytea pk`, `reason`, `created_at` (abuse; checked at finalize).
**media.media_items** — `resource_id pk`, `storage_object_id`, `media_type smallint` (image, animated-image, svg, video), `width int null`, `height int null`, `duration_ms int null`, `frame_count int null`, `has_alpha bool null`, `color_space varchar(30) null`, `icc_profile_name varchar(100) null`, `orientation smallint null`, `video_codec`/`audio_codec varchar(30) null`, `has_gps_metadata bool` (Q6), `browser_playable bool`.
**media.media_derivatives** — `id uuid pk`, `media_id`, `kind varchar(30)` (thumb-320, preview-1600, poster, preview-mp4, svg-safe, svg-raster), `storage_key`, `mime`, `width`, `height`, `size_bytes`, `status` (pending, ready, failed, skipped), `error null`, `created_at`. Unique `(media_id, kind)`.
**snippets.snippets** — `resource_id pk`, `language varchar(40) null` (user pick; detection is client-side highlight.js per tech stack), `render_mode smallint` (code, plain, markdown, terminal), `current_version int`, `version_count int`, `updated_at`.
**snippets.snippet_versions** — `id uuid pk`, `snippet_id`, `version int` (unique with snippet_id), `content text` (≤ plan cap, §3.2), `content_sha256 bytea`, `size_bytes int`, `line_count int`, `has_ansi bool`, `created_via`, `created_at`. Versions are immutable; edit = new row under `SELECT … FOR UPDATE` on the parent (tech stack: "every edit adds a new row"). Diff: **versions of one snippet** (E1.1 side-by-side diff) via DiffPlex on the server; **two arbitrary snippets** only for the owner of both (Q10).

### 2.4 Retention (config `Plans:*`, PRD pricing)
| | Guest | Free | Starter | Pro |
|---|---|---|---|---|
| Max file / media | none (Q4) | 25 MB | 100 MB | 5 GB |
| Snippet max | 256 KB | 512 KB | 2 MB | 5 MB (Q9) |
| Default = max expiry | 7 d (snippets only) | 7 d | 30 d | null = permanent (Q1) |
| Burn After Downloading | ✗ | ✓ | ✓ | ✓ |
| Total storage quota | — | 250 MB? | 5 GB? | 200 GB? (Q3) |
Resource `expires_at` drives the bound link's `expires_at` (LO-A/C). On expiry: object bytes queued for deletion at `bytes_delete_after` (proposal: = `expires_at`, no 90-day blob quarantine); metadata + link follow normal link quarantine → 410 page.

### 2.5 Migrations (names only, expand-only)
- `resources`: `Initial` (resources, download_grants, storage_usage, idempotency_keys).
- `files`: `Initial` (upload_sessions, storage_objects, file_items, storage_deletions, processing_tasks), `AddBlockedContentHashes`.
- `media`: `Initial` (media_items, media_derivatives, processing_tasks).
- `snippets`: `Initial` (snippets, snippet_versions).
- `links` (LO-A/C only): `AddResourceTarget` (`target_kind`, `resource_id`, index `CONCURRENTLY` per MUST NOT #32 — needs a raw-SQL migration with `suppressTransaction`).
- `identity`: `AddDeviceAuthorizations` (only if BE-024 device flow is chosen). Scopes need no migration (`text[]`).
- `analytics` (optional, BE-025): `AddClickEventKind` (`kind smallint not null default 0` on partitioned `click_events`).
- `billing`: none (`Tier` stored as int).

---

## Part B: upload pipeline, burn and background jobs

## 4. Upload pipeline
1. **Session** (Phase A/B/C): resolve principal (invalid creds → 401, never guest); plan limits; `Upload-Length ≤ maxFileBytes` else 413 before any byte; tx with quota advisory lock: `reserved_bytes += length` if `used+reserved+length ≤ quota` else 403 `quota-exceeded`; insert session (`temp/<sessionId>`).
2. **Transport**: Nginx (`client_max_body_size 64m`, `proxy_request_buffering off`, `proxy_http_version 1.1`) → Kestrel tus endpoint with per-endpoint `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = 64 MiB`, `MinRequestBodyDataRate` 240 B/s grace 10 s, no form parsing. tusdotnet `TusDiskStore` rooted at `storage/temp/tus` (same filesystem as `objects` so promote is an atomic rename). Each PATCH updates `received_length`/`last_activity_at`.
3. **Complete → finalize task**: `/complete` validates `received == declared`, writes resource row (`processing`), file/media row, link (LO-A/C via `ILinkIssuer`, expiry = resource expiry), idempotency completion, and a `files.processing_tasks(scan)` row — one tx per module, ordered so a crash leaves a re-runnable task (same "ordered, idempotent" approach as `PurgeService.cs:19-24`). The link is active immediately (so the client can copy it at once); the descriptor answers `processing` and no ticket is minted until the object is clean.
4. **Identify** (Worker): read first 64 KB → MimeDetective; one sequential read computes SHA-256 + size. Declared type/extension ignored. Allowlist per kind: media = PNG, JPEG, GIF, WebP, AVIF, HEIC (Q7), SVG (XML sniff + root `<svg>`), MP4/MOV/WebM/MKV; file = any, but executables/scripts (PE, ELF, Mach-O, MSI, `.lnk`, `.bat/.ps1/.js` by detected type) blocked on Free/Guest (Q8). Mismatch kind (e.g. "media" but PDF) → re-kind to file or reject (Q7). Blocked hash → `rejected/blocked-hash`.
5. **Scan**: clamd via nClam. Files ≤ `Clam:StreamMaxBytes` use INSTREAM; larger use `SCAN <path>` on a read-only shared volume. clamd limits: `MaxScanSize`/`MaxFileSize` ≈ 4000M, `MaxRecursion 16`, `MaxFiles 10000`, `AlertExceedsMax yes`, `AlertEncryptedArchive` (Q8). Result: clean → continue; FOUND → move to `quarantine/<uuid>`, `rejected/infected`, keep `Abuse:EvidenceRetentionDays`, increment owner abuse signal; error/timeout → `scan_status=error`, retry with backoff (1, 5, 15, 60 min, max 6), then `rejected/scan-unavailable` (fail-closed: content is never served unscanned). Over hard limit → `skipped-too-large` → policy Q8.
6. **Filename**: strip path, control chars, bidi overrides (U+202A–202E, 2066–2069), trailing dots/spaces, Windows reserved names; NFC; ≤ 255 bytes; extension forced to match detected type for executables-looking names. Stored only as metadata; never used for paths.
7. **Promote**: `rename(temp → objects/<shard>/<uuid>)` where shard = first 2 bytes of `SHA-256(uuid)` hex (R6); fsync dir; update `storage_objects.area`, `processing_status=ready`, convert reservation to `used_bytes` in the quota tx; enqueue media derivative task; invalidate caches.
8. **Derivatives** (Media, Worker, never touching the original): NetVips `thumbnail` from file path with `fail_on: error`, `access: sequential`, max 100 MP input (larger → skip derivatives, original still downloadable), output WebP/PNG with metadata stripped and colour converted to sRGB (preview only; original keeps its ICC). Animated GIF/WebP → poster + first frames. Video: `ffprobe -v error -show_streams -show_format -of json` with `-protocol_whitelist file`, timeout 60 s, `nice`, cgroup/CPU limit; poster at 1 s; H.264/AAC `preview-mp4` only when not browser-playable (HEVC/ProRes). Worker concurrency `Media:MaxParallel` (default 2).
9. **SVG**: original kept byte-for-byte, served **only** as attachment. Viewer uses (a) `svg-raster` PNG from NetVips/librsvg after (b) a sanitized `svg-safe` copy (drop `<script>`, `<foreignObject>`, `on*` attrs, `javascript:`/external `href`, `<!ENTITY>`/DOCTYPE; no external fetch). Both served from the content host with `CSP: sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:`.
10. **Inline viewing** only for derivatives and allowlisted raster/video originals ("zero-compression" = original bytes shown at 100 %): `Content-Type` from detection, `nosniff`, sandbox CSP, content host only. PDF/HTML/XML/SVG originals are never inline (react-pdf fetches bytes as attachment via ticket).

---

## 5. Burn After Downloading
**Definition proposed (needs PO sign-off):** a burn file is consumed when the **first non-owner GET of its content bytes** reaches the content host with a valid burn ticket. Viewing the zaa.gl link, the viewer page, the descriptor, previews/thumbnails (never generated for burn files, Q13), `HEAD`, owner downloads and link-preview bots do **not** consume it.
- **Bots**: tickets are minted only by `POST …/download` (preview crawlers fetch with GET and never POST); ticket TTL 120 s and single-claim; never decided by User-Agent (R10). Optional Turnstile on the POST for burn files (Q12).
- **Atomic claim** (first GET of `/d/{ticket}`, one tx, `lock_timeout 3s`):
  ```sql
  UPDATE resources.resources
     SET burn_claimed_at = @now, burn_grant_id = @grant, download_count = download_count + 1, updated_at = @now
   WHERE id = @rid AND burn_after_download AND burn_claimed_at IS NULL
     AND deleted_at IS NULL AND disabled_at IS NULL AND processing_status = 2
     AND (expires_at IS NULL OR expires_at > @now) AND (purge_after IS NULL OR purge_after > @now)
  RETURNING id;
  -- 0 rows → classify with a plain SELECT: burn_claimed_at set → 410 burned; else 404/410 as state says
  INSERT INTO resources.download_grants (id, resource_id, kind, created_at, expires_at, max_requests, requests_used, first_request_at)
  VALUES (@grant, @rid, 2, @now, @now + @grace, @maxReq, 1, @now);
  ```
  Concurrent requests: exactly one UPDATE matches (row lock + re-evaluated WHERE); losers get `410 burned`. Run via raw Npgsql (not `SqlQuery`, R12).
- **Interrupted / ranged downloads**: the winning grant may be reused for `Range` requests (download managers, resume) while `now < expires_at` (`Burn:GraceMinutes`, default 30) and `requests_used < max_requests` (default 16): `UPDATE download_grants SET requests_used = requests_used + 1 WHERE id=@g AND expires_at > @now AND requests_used < max_requests RETURNING 1`. The ticket URL stays the same, so resume works only for the client that holds it. After grace → bytes deleted. A dropped download after grace is lost (Q14).
- **Finalize job** (every minute): `SELECT r.id FROM resources.resources r JOIN resources.download_grants g ON g.id = r.burn_grant_id WHERE r.burned_at IS NULL AND g.expires_at <= @now ORDER BY g.expires_at LIMIT 200 FOR UPDATE OF r SKIP LOCKED` → `burned_at = @now`, insert `files.storage_deletions` (via Files contract, after commit; idempotent), release quota. Link (LO-A) shows 410 "downloaded and deleted"; metadata purged on the normal schedule.
- **Interaction**: password → claim requires a valid viewer token; processing → `409 processing` (no claim); combined with "after X days" → whichever first; owner can't toggle burn after a claim.
- **Product questions**: Q11–Q16 below.

---

## 6. Background jobs (all `BackgroundService` + `PeriodicTimer` + `TimeProvider`, claims via `FOR UPDATE SKIP LOCKED` in short tx, external work outside tx — spec §7.8)
| Job | Host | Cadence | Work |
|---|---|---|---|
| ScanTaskRunner | Worker | continuous (poll 2 s, LISTEN optional) | claim `files.processing_tasks(scan)` with `locked_until`; identify → scan → promote/quarantine (§4.4–4.7) |
| DerivativeTaskRunner | Worker | continuous | `media.processing_tasks`; NetVips/FFmpeg with timeouts; failures → derivative `failed`, never the resource |
| ScanRetry | Worker | 5 min | `scan_status=error AND attempts<6 AND run_after<=now` → re-enqueue; then reject + owner email |
| UploadSessionExpiry | API or Worker | 15 min | sessions `open` idle > 24 h or `expires_at` past → delete temp via tusdotnet store, release reservation, `expired` |
| ResourceExpiry/Purge | API (light) | 15 min | `bytes_delete_after <= now` → queue bytes deletion, release quota; `purge_after <= now` → PurgeResource (re-check under lock, abuse evidence first, delete rows); mirrors `PurgeService.cs:65-124` |
| BurnFinalize | API | 1 min | §5 |
| StorageDeletionDispatcher | Worker | 1 min | delete keys from `files.storage_deletions` (idempotent; missing file = success) |
| StorageReconciliation | Worker | daily, off-backup window | list `objects/` + `temp/` + `quarantine/`; file without DB row and older than 24 h → move to `orphans/` then delete after 7 d; DB row without file → metric + alert + mark resource `error`; recompute `storage_usage` per owner and log drift |
| QuarantineRetention | Worker | daily | delete quarantined objects past evidence retention |
| GrantCleanup / idempotency cleanup | API | hourly | delete expired grants (> 1 d) and idempotency rows (> 24 h, spec §4.4) |
Wolverine: **not adopted in Phase 1** (unused today; adds a second job model and its own PostgreSQL schema). The task tables give durable, observable queues with the existing pattern; revisit with the architect (Q17).

---

## Part C: backend tasks BE-001 to BE-029

## 7. Tasks

**BE-001 · P0 · Enforce API-key scopes; add content scopes**  
Add `RequireScope("x")` policies (JWT passes all; ApiKey must hold the claim); apply to existing links/stats endpoints and all new ones; add `snippets:write`, `files:write`, `resources:read` to `AllowedScopes`. Files: `Program.cs:174-186`, `Endpoints/*.cs`, `IdentityService.cs:39`, `TokenService.cs:60`. Deps: —. API: 403 `insufficient-scope`. DB: none. AC: a `links:read` key gets 403 on `POST /api/links`; JWT unaffected. Tests: endpoint matrix scope × route.

**BE-002 · P0 · RFC 7807 `type` URIs and new error codes**  
Map `Error.Code` → `https://zaa.gl/problems/<name>`, add codes listed in §3. Files: `ApiResults.cs:8-27`, `SharedKernel/Result.cs`. Deps: —. AC: every problem has `type`,`title`,`status`,`code`; 413/415/410/460 supported. Tests: unit per code.

**BE-003 · P0 · Pro tier and content limits**  
`PlanTier.Pro=2`; `PlanLimits` gains `MaxFileBytes, MaxSnippetBytes, ContentRetentionDays?, StorageQuotaBytes, BurnAllowed, IsPaid, KeepsStatsAfterPurge, ApiKeys`; replace tier equality checks; `Stripe:ProPriceId` + webhook mapping by price. Files: `BillingContracts.cs:3-34`, `BillingService.cs:58,149,164`, `PurgeService.cs:102`, `IdentityService.cs:395`. Deps: Q1, Q3. DB: none. AC: Pro archives stats and can create keys; limits come from config. Tests: plan matrix, webhook Pro/Starter.

**BE-004 · P0 · IFileStorage v2 + LocalFileStorage**  
Replace the unused interface: `PutAsync(area, stream) → {key,size,sha256}`, `OpenReadAsync(key, offset, length?)`, `StatAsync`, `PromoteAsync(tempKey) → objectKey` (atomic rename, same FS), `DeleteAsync` (idempotent), `ListAsync(area, olderThan)`, `GetInternalPath(key)` for X-Accel. Random-bit sharding; keys validated by regex (no traversal); `Storage:Root`. Files: `SharedKernel/Storage/*`. Deps: —. AC: no path from user input; rename atomic. Tests: traversal keys rejected, range reads, concurrent promote, delete-missing ok.

**BE-005 · P0 · Content/upload host, headers, Kestrel limits**  
`Hosts:Content` (+ optional `Hosts:Upload`), host-first routing decision; content-host middleware (no cookies, sandbox CSP, nosniff, CORP, `Referrer-Policy`), per-host forwarded-header trust; Kestrel: global `MaxRequestBodySize` stays small, tus/simple endpoints raise per request; `MinRequestBodyDataRate`; CORS for tus (`Upload-*`, `Location`, `Tus-*` exposed) for `app.zaa.gl` only. Files: `HostRouting.cs:3-75`, `Program.cs:47-67,222-239`, `appsettings.json`. Deps: devops (Nginx/DNS). AC: API paths on content host → 421 and vice versa; no `Set-Cookie` on content/short hosts. Tests: `HostRoutingTests` extension.

**BE-006 · P0 · Resources module skeleton**  
DbContext `resources`, `Initial` migration, `AddResourcesModule`, state calculator, `IResourceDirectory`/`IResourceLifecycle`/`IStorageQuota` contracts, idempotency helper. Files: `Modules/Resources/**`, `Program.cs:193-199`. Deps: BE-004. DB: §2.5. AC: migrator lists `resources`; state calculator total. Tests: state table tests like `LinkStateTests`.

**BE-007 · P0 · Link-to-content binding (per architect LO-A/B/C)**  
LO-A/C: `ILinkIssuer.CreateForResourceAsync` reusing Phase A–D, lease, alias lock, idempotency, skipping URL normalize/reputation/blocklist; `links.AddResourceTarget`; snapshot + redirect SQL carry kind/resource id; redirect 302 to viewer with pass (Q15). LO-B: `/s|f|i/{publicId}` resolution + 1–3-char guard (R15). Files: `LinkService.cs`, `LinkContracts.cs:11-22,203-243`, `LinkResolver.cs:58-125`, `ResolutionService.cs:49-108`, `ResolutionEndpoints.cs:16`. Deps: architect. AC: content link uses leased codes, aliases obey §4.5; deleting resource disables link. Tests: concurrency tests of spec §7.2 re-run for content links.

**BE-008 · P0 · Snippets CRUD + versions + public read + raw**  
Domain, `Initial` migration, service, endpoints in `SnippetsEndpoints.cs`, raw on content host. Deps: BE-006, BE-007, BE-001. AC: edit creates version n+1 under row lock; `baseVersion` conflict 409; size caps per plan. Tests: concurrent edits, caps, raw headers, gates (password/expired/disabled).

**BE-009 · P1 · Markdown + diff**  
Markdig+HtmlSanitizer renderer with cache; DiffPlex side-by-side endpoint with limits. Deps: BE-008. AC: XSS corpus (script, `javascript:`, SVG in md, data URIs) yields inert HTML; diff limit 413. Tests: sanitizer corpus, diff golden files.

**BE-010 · P0 · CLI text ingest**  
`POST /api/snippets/raw` streaming with byte counter, UTF-8 validation, ANSI keep/strip, `Accept: text/plain` response. Deps: BE-008. AC: 10 MB pipe to Free → 413 without buffering whole body. Tests: streamed oversize, invalid UTF-8, CRLF preservation.

**BE-011 · P0 · tus upload sessions**  
tusdotnet on `/api/uploads` (+ simple upload), `files.Initial` migration, quota reservation, ownership authorize, expiration. Deps: BE-004, BE-005, BE-006, BE-003. AC: 5 GB Pro upload resumes after kill; Free 26 MB rejected at create; other user's session 404. Tests: offset mismatch, parallel PATCH, reservation release on DELETE/expiry.

**BE-012 · P0 · Finalize + identification**  
`/complete`, MimeDetective allowlist, SHA-256, filename sanitizer, hash blocklist, processing task enqueue. Deps: BE-011. AC: declared type never trusted; polyglot/renamed exe handled per Q8. Tests: fixture corpus (bidi name, zero-byte, polyglot, mislabeled PNG).

**BE-013 · P0 · Worker host + task queue**  
Make `Zaagl.Worker` real: remove template, register module services without API-only hosted services (split `AddXModule`/`AddXJobs` in Links/Identity/Analytics), SharedKernel `SkipLockedTaskQueue` helper (raw Npgsql), health/metrics. Files: `Zaagl.Worker/*`, `LinksModule.cs:54,98,106-116`, `IdentityModule.cs:40-46`, `AnalyticsModule.cs:32-33`. Deps: —. AC: Worker runs no CreationGatePoller/LISTEN; two workers never process one task. Tests: claim race test.

**BE-014 · P0 · ClamAV scan + quarantine**  
nClam client, INSTREAM/SCAN path switch, retries, quarantine move, abuse signal. Deps: BE-012, BE-013. AC: EICAR rejected and never downloadable; clamd down → stays processing, then fail-closed. Tests: fake clamd (clean/found/timeout), EICAR in ZIP.

**BE-015 · P1 · Media metadata + derivatives**  
`media.Initial`, NetVips/FFmpeg runners with limits, `GET /api/media/{id}`. Deps: BE-014. AC: original SHA-256 unchanged after processing; 16-bit PNG/ICC preserved in original; decompression bomb skipped. Tests: fixtures (CMYK JPEG, APNG, HEVC MOV, 30k×30k PNG).

**BE-016 · P1 · SVG safe view**  
sanitizer + raster derivative + headers. Deps: BE-015. AC: SVG with script/foreignObject/external href never executes or fetches. Tests: SVG attack corpus.

**BE-017 · P0 · Download tickets + X-Accel delivery**  
HMAC tickets (`kid`, `Content:TicketKeys`), grants, `/d/{ticket}` endpoint on content host, headers, owner vs public, Range via Nginx. Deps: BE-005, BE-006, BE-004. AC: expired/forged ticket 403; ticket for resource A can't fetch B; Range works. Tests: ticket tamper, TTL via `FakeTimeProvider`, header snapshot.

**BE-018 · P0 · Burn After Downloading**  
claim SQL, grant reuse, finalize job, 410 page states. Deps: BE-017, Q11–Q16. AC: 50 concurrent first GETs → exactly one 200, rest 410; ranged resume within grace ok; bytes deleted after grace. Tests: concurrency against real Postgres, crash between claim and finalize.

**BE-019 · P0 · Resource expiry/purge + link coupling**  
job + PurgeResource routine + deletion outbox dispatcher. Deps: BE-006, BE-007. AC: no expired resource ever served (invariant like spec §4.3); bytes gone at `bytes_delete_after`. Tests: purge race with download, re-check under lock.

**BE-020 · P1 · Orphans + reconciliation**  
upload expiry, orphan sweep, drift report. Deps: BE-011, BE-013. AC: stray file in `objects/` removed after 7 d; missing file alerts. Tests: temp FS fixtures.

**BE-021 · P1 · Quotas endpoint + ledger recompute**  
`/api/me/storage`, recompute job, downgrade behaviour (Q3). Deps: BE-011. AC: reserved+used never exceed quota under 20 parallel creates. Tests: concurrency.

**BE-022 · P1 · Abuse for content**  
takedown of resource (+ linked link), content-hash blocklist admin API, reports by code covering content, evidence retention (object kept in quarantine). Files: `AbuseControls.cs:93-124`, `AdministrationEndpoints.cs`. Deps: BE-006, BE-012. AC: takedown blocks downloads within cache bound; hash re-upload rejected. Tests: takedown→download 410.

**BE-023 · P1 · Account deletion + export**  
`IResourceDataEraser` called from Identity; export includes resources metadata (no bytes). Files: `IdentityService.cs:442,476-481`. Deps: BE-006. AC: deleting account queues all bytes for deletion. Tests: extend `AccountDeletionTests`.

**BE-024 · P1 · CLI/desktop auth**  
device authorization grant (or Free scoped keys, Q5), `/api/clients/config`. Deps: BE-001, Q5. DB: `identity.AddDeviceAuthorizations`. AC: CLI login without browser cookies; codes single-use, 10 min. Tests: polling `slow_down`, expiry.

**BE-025 · P2 · View/download analytics**  
event kind in click pipeline or resource counters only (no IP, MUST NOT #26). Deps: BE-017. Tests: guest content gets counter only.

**BE-026 · P1 · Rate-limit policies + guest snippets (if approved)**  
new limiter policies, guest snippet path with CAPTCHA + guest quota. Files: `Program.cs:69-86`, `GuestCreation.cs`. Deps: Q4. Tests: limiter partitions.

**BE-027 · P1 · Test harness for content**  
temp storage root per test, fake clamd TCP server, fixture corpus, `FakeTimeProvider`. Files: `tests/Zaagl.Files.Tests`, `tests/Zaagl.Snippets.Tests`. Deps: BE-004. AC: runs against local PG 5433 without Docker.

**BE-028 · P2 · Docs + OpenAPI**  
`docs/API.md`, DECISIONS (LO choice, burn semantics, Wolverine deferral), OpenAPI tags for CLI/desktop teams. Deps: all.

**BE-029 · P2 · Observability**  
metrics: upload bytes, open sessions, scan latency/queue depth, derivative failures, disk free per area, burn claims; alerts at 80/90 % disk. Deps: BE-013.


Critical path: BE-001/002/003/004 → BE-005/006/013 → BE-007 (architect) → BE-008/011 → BE-012 → BE-014 → BE-017 → BE-018/019.

---


Added during consolidation: BE-030 and BE-031 (see IMPLEMENTATION_PLAN.md, Stage 1).

## Part D: storage provisioning (DevOps draft)

## 2. Storage provisioning

### 2.1 Disks and filesystems (target VPS per TS §16: 12–16 vCPU, 64 GB, 2 TB NVMe)
| Mount | Device | FS | Holds | Why |
|---|---|---|---|---|
| `/` | OS disk (≥ 150 GB) | ext4 | OS, `/var/lib/docker` (images, json logs) | Docker logs can't fill data disks |
| `/srv/zaagl/postgres` | own volume/LV (≥ 300 GB [EST]) | ext4 or XFS | `pgdata`, `pg_wal`, pgBackRest spool | TS §16: "uploads can never fill the database's disk" |
| `/srv/zaagl/storage` | **dedicated data disk** (≥ 2 TB, expandable; RAID1 if dedicated server) | **XFS with `prjquota`**, `noatime,nodev,nosuid,noexec` | `objects/ thumbnails/ previews/ temp/ quarantine/` (+ proposed `transfers/`) | one filesystem so temp→objects is an atomic `rename(2)` (a 5 GB copy otherwise); per-directory hard caps via XFS project quotas |

Project quotas [EST, revise after Q1/Q2]: `temp` 200 GB hard (≈ 40 concurrent 5 GB uploads), `quarantine` 50 GB, `previews+thumbnails` 15 % of disk, `objects`/`transfers` = remainder. Do **not** put `temp` on tmpfs or the OS disk.

### 2.2 Layout and permissions
```
/srv/zaagl/storage/            root:zaagl        0750
  objects/aa/bb/<uuidv7>       zaagl:zaagl-read  2750 dirs / 0640 files   (long-lived originals, backed up)
  transfers/aa/bb/<uuidv7>     zaagl:zaagl-read  2750 / 0640              (PROPOSED: ephemeral/burn files, NOT backed up, Q3)
  thumbnails/ previews/        zaagl:zaagl-read  2750 / 0640              (regenerable, not backed up)
  temp/                        zaagl:zaagl       0700                     (tus partials; nginx has no access)
  quarantine/                  zaagl:zaagl       0700                     (infected/suspect, admin-only)
/srv/zaagl/secrets/            root:zaagl        0750, files 0440
```
- Fixed IDs baked into images: `zaagl` UID/GID 10001 (api, worker), `zaagl-read` GID 10002 added to nginx via `group_add`; clamd gets `group_add: [10001]` and a **read-only** mount of `temp/` (scan by path, see §4).
- Mounts: api rw `temp,objects,transfers,quarantine`; worker rw `objects,transfers,thumbnails,previews,temp,quarantine`; nginx **ro** `objects,transfers,thumbnails,previews` only; web and coordinator: none.
- File names are generated IDs only (TS §08); original name, MIME, size, SHA-256 live in Postgres.

### 2.3 Capacity planning (all numbers [EST])
Assumptions: 12 months after Phase 1 launch, 20 000 registered users, 15 % upload in a given month (3 000 uploaders), uploader mix 80 % Free / 12 % Starter / 8 % Pro. Limits from PRD §6.

| Tier | Uploaders | Cap / retention | Behaviour assumed | Resident storage |
|---|---|---|---|---|
| Free | 2 400 | 25 MB, 7 days | 8 files/week × 4 MB | ≈ 77 GB |
| Starter | 360 | 100 MB, 30 days | 30 files/month × 12 MB | ≈ 130 GB |
| Pro (ephemeral, 30-day default) | 240 | 5 GB | 20 GB resident each | ≈ 4.8 TB |
| Pro (PRD "permanent") | 240 | 5 GB | +20 GB/user/month, never expires | **+4.8 TB per month, unbounded** |
| Derived (thumbs/previews) | – | – | ≈ 10 % of image/video bytes | ≈ 50–150 GB |
| temp + transcode scratch | – | – | 40 × 5 GB worst case + 2 × 10 GB FFmpeg scratch | 200–220 GB reserved |

Conclusion: Free/Starter fit comfortably on 2 TB; **Pro dominates and does not fit without a per-account storage quota** (e.g. 100 GB active per Pro account [EST]) plus default expiry. Even 240 × 100 GB = 24 TB worst case, so quotas are overcommitted and need the admission control in §2.4. Bandwidth: 240 Pro × 30 GB uploaded/month × 2 downloads ≈ 14 TB egress + 7 TB ingress per month ≈ 65 Mbit/s average, peaks saturating 1 Gbit/s [EST]. Many VPS plans cap monthly traffic (often ~20 TB): check the provider plan (Q6). Restic repo ≈ 1.1–1.3 × backed-up bytes (media dedupes poorly) [EST].

### 2.4 Disk-full behaviour (contract with backend)
1. **Admission at session creation**: tus `Upload-Length` mandatory (no deferred length); API rejects when `free_bytes − Σ(in-flight declared bytes) − 5 % safety < declared` → `507 Insufficient Storage` + `Retry-After` (or 503). Per-account quota checked at the same point.
2. Thresholds on `/srv/zaagl/storage` used %: **70 %** warn; **80 %** page + "soft close" (new sessions capped at 100 MB, Free uploads paused); **90 %** refuse all new file sessions (snippets keep working, they live in Postgres); **95 %** emergency runbook (accelerate expiry, purge abandoned temp, extend volume). Downloads and redirects are never blocked by storage state.
3. Temp hygiene: tus partials older than 24 h (configurable) deleted by worker; quota hit on `temp` makes PATCH fail and the client resumes later.
4. Postgres volume: warn 75 %, page 85 %; `pg_stat_archiver.failed_count` rising = WAL piling up in `pg_wal` (page).
5. Inodes monitored as well as bytes. Expiry/burn job lag is an SLO (files past expiry still on disk > 15 min = alert): it is both a capacity and a privacy promise.

---

