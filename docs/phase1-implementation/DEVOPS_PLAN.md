# Phase 1 DevOps plan

| | |
|---|---|
| Owner | zaagl-devops |
| Status | PLANNING, awaiting approval. No Phase 1 code exists yet. |
| Source | _drafts/devops.md (whole), written 2026-11-09 by the planning agent named above and kept verbatim below this header |
| Decisions | Numbered PD-nn in [DECISIONS.md](DECISIONS.md). Draft question ids (D-n, Qn, Q-nn) map to PD entries there. |

## Consolidation notes

These are the lead architect's overrides. Where they differ from the draft text below, they win.

- **User-content host (PD-03, PD-38, approved):** a `zaa.gl` subdomain, `usercontent.zaa.gl`, proxied by Cloudflare. No domain purchase. The Cloudflare terms question is deferred to before launch (PD-52).
- **Upload host (PD-12, approved):** tus uploads on `api.zaa.gl` through Cloudflare in chunks of 90 MB or less. The draft's `dl.<ucd>` proposal below is superseded (see §3, updated 2026-11-09).
- **Valkey and PostgreSQL version (PD-26):** open.
- **Environment:** Docker cannot run on the development machine (firmware virtualisation is off). Container-based tasks are verified in CI or on a staging host and are never claimed as verified locally.
- The draft's open questions are mapped in DECISIONS.md (devops Q1–Q14).

---

# Phase 1 (micro-sharing) — DevOps / SRE plan (DRAFT)

Author role: zaagl-devops agent. Status: planning only, nothing in compose/Docker/deploy/config/source was changed.
Sources: `Zaagl-Frontend/src/Doc/zaagl-tech-stack (2).html` (TS §n), `zaagl-prd.html` (PRD), `docs/spec/zaagl-spec-v9.4.md` (spec §n), plus the repository files cited inline.
Convention: **[E]** = evidence read in a file; **[EST]** = estimate/assumption, to be replaced by measurement; **[UNVERIFIED-DOCKER]** = cannot be verified on the dev machine because Docker is not installed (firmware virtualisation off); **[CRED]** = blocked on credentials/purchases.

---

## 1. Audit of what exists

### 1.1 Findings (dev-only vs production-ready)

| # | Finding | Evidence | Class |
|---|---|---|---|
| A1 | Single root `docker-compose.yml`, self-described as "Local development stack"; spec §3 wants `deploy/docker-compose.yml` + `deploy/docker-compose.override.yml` + `deploy/.env.example`. None of the deploy files except `init-db/` and `secrets/README.md` exist. | `docker-compose.yml:1-5`, `deploy/` listing | dev-only |
| A2 | Only one network `backend` (plain bridge, **not** `internal: true`); no `edge`, no `egress`; no fixed `ipv4_address` for proxy/web. | `docker-compose.yml:146-148` | dev-only |
| A3 | Postgres `5433`, Valkey `6379`, coordinator `5085`, api `5084`, Mailpit published on the host. Valkey has no password. Production rule: only the proxy publishes ports. | `docker-compose.yml:14,31,77,109` | dev-only, **must not ship** |
| A4 | All secrets inline as env vars (`POSTGRES_PASSWORD: zaagl_dev`, `Jwt__Key`, coordinator tokens, `GUEST_KEY`); no `secrets:` section; app has no key-per-file config provider. | `docker-compose.yml:11-13,49-51,63-65,95-101` | dev-only |
| A5 | No resource limits (`mem_limit`, `cpus`, `DOTNET_GCHeapHardLimit`), no `logging:` rotation, no `oom_score_adj`, no `read_only`. Spec §3 requires them. | `docker-compose.yml` (absent) | gap |
| A6 | Image `postgres:18` vs spec §2/§3 "PostgreSQL 17"; TS §01 says 18. Volume mount `/var/lib/postgresql` matches the PG18 image layout. Needs an ADR, not a change. | `docker-compose.yml:8,17` | conflict |
| A7 | Valkey present and **hard-required**: `Program.cs` throws if `ConnectionStrings:Valkey` missing and `/health/ready` includes the Valkey check. Spec §9.6 says a Redis container is added only later; TS §03/§10 says Valkey "Recommended". | `src/Zaagl.Api/Program.cs:33-34,55-61`; spec §9 item 6 | conflict |
| A8 | No WAL archiving, no pgBackRest, no file backup; coordinator runs `RequireArchiveConfirmation=false`. Spec §3 makes WAL archiving mandatory in staging/prod. | `docker-compose.yml:72`; `docs/TRACEABILITY.md:39`; `docs/RUNBOOK.md` "Quarterly restore drill … Not automated" | gap, P0 |
| A9 | No Nginx / proxy service, no TLS, no Cloudflare real-IP handling anywhere in either repo. | repo-wide | gap, P0 |
| A10 | `ForwardedHeadersOptions`: `KnownProxies` and `KnownIPNetworks` both **cleared** → ASP.NET Core then accepts `X-Forwarded-For` from any sender. Spec §7.13/§9 require the proxy's exact fixed address. `X-Forwarded-Host` is not enabled although the BFF contract relies on it. | `src/Zaagl.Api/Program.cs:47-53` | dev-only, security |
| A11 | `AllowedHosts: "*"`; `Hosts` options only know `Short` and `Api`, anything else → 421. Phase 1 needs a third host class (user-content / transfer). | `src/Zaagl.Api/appsettings.json`; `src/Zaagl.Api/Infrastructure/HostRouting.cs:5-11,33` | gap (backend) |
| A12 | No Kestrel limits configured → defaults (30 MB `MaxRequestBodySize`). Fine for API; the tus endpoint must override per-request. No tus/tusdotnet code yet; `src/Modules/Files` and `src/Modules/Media` contain only `.csproj` + empty folders. | `src/Zaagl.Api/Program.cs` (no `ConfigureKestrel`); module listing | gap (backend) |
| A13 | `Zaagl.Worker` is the `dotnet new worker` template (logs every second with `DateTimeOffset.Now`, a banned API) and has **no Dockerfile** and no compose service. | `src/Zaagl.Worker/Worker.cs`, `Program.cs` | gap |
| A14 | Dockerfiles: `COPY . .` with **no `.dockerignore`** (sends `.git`, `bin/`, `obj/`, `graphify-out/` into context, breaks layer caching); `useradd -r` without fixed UID/GID (storage permissions need stable IDs); floating tags `sdk:10.0`/`aspnet:10.0`; `curl` installed only for healthchecks. Non-root user is good. | `src/Zaagl.Api/Dockerfile`, `src/Zaagl.Coordinator/Dockerfile` | partly ready |
| A15 | Coordinator image not tagged by input hash (spec §3 "Image tags"). No `ops` container (documented deviation). | `docs/RUNBOOK.md` "Host lost entirely" | gap (known) |
| A16 | No OpenTelemetry wiring (spec §2); logs are Serilog console text, not JSON. No `/metrics`. | `src/Zaagl.Api/Program.cs:25-29` | gap |
| A17 | **No CI in either repo**: no `.github/` in `Zaagl-Backend` or `Zaagl-Frontend`. Remotes are `github.com/Samira-Elsaeidy/Zaagl-{Backend,Frontend}`. | filesystem | gap, P0 |
| A18 | Integration tests hard-code `Host=localhost;Port=5433;…;Password=zaagl_dev` (no Testcontainers despite spec §2). CI must provide Postgres on 5433 or tests must read an env var. | `tests/Zaagl.Links.Tests/PurgeJobTests.cs:21`, `tests/Zaagl.Coordinator.Tests/StartupCheckTests.cs:30,192` | CI constraint |
| A19 | Banned-API analyzer downgraded to warning. | `Directory.Build.props` | known debt |
| A20 | Frontend has no Dockerfile and `next.config.ts` lacks `output: "standalone"` (needed for a small self-hosted Node image, TS §04). | `Zaagl-Frontend/next.config.ts` | gap (frontend) |
| A21 | `docs/RUNBOOK.md` and `docs/SCALING.md` cover links/coordinator only: nothing on storage, disk-full, ClamAV, uploads, file restore. Init SQL creates `api_user`/`coordinator_user` with password `zaagl_dev` (unused). | `docs/RUNBOOK.md`, `deploy/init-db/01-extensions-and-roles.sql` | gap |
| A22 | Product conflict: PRD §6 Pro = "Up to 5 GB / file, **permanent storage**"; PRD risk register suggests Cloudflare R2; TS §08 chose local disk; this Phase 1 brief says ephemeral + burn-after-download. Capacity planning depends on the answer (Q1). | PRD §6/§7; TS §08 | conflict |

Nothing container-based in this list was run: **[UNVERIFIED-DOCKER]**. Git log (`acc7e44`) claims an earlier full Docker end-to-end run on another machine; not reproducible here.

### 1.2 What can be reused as-is
Healthcheck split (`/health/live` for Docker, `/health/ready` for deploy gating), `migrator` one-shot pattern with `service_completed_successfully`, non-root runtime images, `restart: unless-stopped`, `deploy/secrets/README.md` structure, RUNBOOK DB-restore order (stop api → coordinator → restore → declare-restore).

---

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

## 3. Reverse proxy (Nginx, container `proxy`, the only service publishing 443)

> **Updated 2026-11-09 to the approved layout** (PD-03, PD-12, PD-38, PD-46; Head review stage1-round3-devops note 3).
> The earlier draft's separate user-content domain (`cdn.<ucd>` proxied + `dl.<ucd>` DNS-only, tus with 50 MB chunks
> on `dl.<ucd>`) is **superseded**. DNS records, Cloudflare settings, WAF and rate-limit rules live in
> [design/ops-001-hosts-and-dns.md](design/ops-001-hosts-and-dns.md); the `usercontent.zaa.gl` Nginx server block,
> ticket format and headers live in [design/ARC-008-usercontent-delivery.md](design/ARC-008-usercontent-delivery.md).
> This section only summarises them; where they differ, those two documents win.

### 3.1 Hosts (spec §1.2, PD-03, PD-38). Every host is proxied by Cloudflare; no DNS-only record points at the VPS
| Host | CF mode | Upstream | Serves | Body limit (Nginx) |
|---|---|---|---|---|
| `zaa.gl` | Proxied | `api` (Hosts:Short) | `GET/HEAD /{code}`, 404/410 pages, security.txt; **never cookies** | 1 KB (no bodies; WAF C4 allows GET/HEAD only) |
| `app.zaa.gl` | Proxied | `web` (BFF → api on the internal address `ZAAGL_INTERNAL_API_URL`, not through Cloudflare) | app, viewers (`/p /f /i`), `/activate`, host-only `__Host-` cookies | 2 MB |
| `api.zaa.gl` | Proxied | `api` (Hosts:Api) | `/api/*`, tus uploads (`/api/uploads`, PD-12), `/account/*`, SignalR `/hubs` (websocket); sign-in handshake cookies only (PD-46, host-only, ≤ 10 min); `/health/*` blocked at the edge (WAF C1) | `/api/uploads*`: 90,000,000 bytes; elsewhere 2 MB |
| `usercontent.zaa.gl` | Proxied (PD-38; terms risk B1 / PD-52 deferred) | `api` authorises, Nginx serves bytes via X-Accel-Redirect | user bytes by single-use ticket (`/d/<ticket>`): downloads, inline images/video/PDF previews; **never cookies** | 1 KB; methods GET, HEAD, OPTIONS only (CORS preflight, §3.3) |
| `www.zaa.gl` | Proxied | none (Cloudflare redirect rule) | 301 to `https://app.zaa.gl/` **PROPOSED (U-11)** | – |
| `*.staging.zaa.gl` hosts | Proxied + WAF IP gate (C2) + basic auth | staging VPS | spec §1.2 names; two-level names need ACM or a rename (U-10 / ops-001 B2) | as production |

No session cookie is ever sent to `usercontent.zaa.gl`: no cookie anywhere is set with `Domain=.zaa.gl`, and the
Cloudflare features that set cookies are off (ops-001 §4; a cookie probe runs every 15 min). The desktop app and CLI
upload to `api.zaa.gl` like the browser. PostgreSQL, the coordinator, the worker, ClamAV and `/health/*` have no DNS
name and no published port.

### 3.2 Upload path (tus on `api.zaa.gl`, up to 5 GB, PD-12)
- Client chunk size **≤ 90,000,000 bytes** (Uppy `chunkSize`, desktop/CLI the same), under Cloudflare Free's 100 MB
  request limit [KNOWN]. `location /api/uploads { client_max_body_size 90000000; proxy_request_buffering off;
  proxy_buffering off; proxy_http_version 1.1; client_body_timeout 120s; proxy_send_timeout 300s; proxy_read_timeout 300s; }`.
  Buffering off avoids Nginx spooling chunks into its own temp dir on the OS disk.
- Kestrel raises `IHttpMaxRequestBodySizeFeature` to 90,000,000 only on the tus route; default `MinRequestBodyDataRate`.
- Cloudflare waits 100 s for origin response headers [KNOWN]: a 90 MB `PATCH` at 10 Mbit/s takes about 72 s [EST], so
  slow clients are close to a `524`. **[VERIFY] on staging**; if it fails, lower the client chunk size, not the limit.
- `limit_req zone=upload_create 10r/m burst=10` on `POST /api/uploads`, `limit_conn perip 8` on the upload location,
  `client_header_timeout 15s`, `keepalive_timeout 30s`, `reset_timedout_connection on`.
- api restarts do not lose uploads (tus resumes from `Upload-Offset`): useful for deploys.
- All upload traffic now passes through Cloudflare (the old draft's reason for a DNS-only host). Bandwidth through the
  CDN is part of the B1 / PD-52 terms question, not a size problem.

### 3.3 Download path (`usercontent.zaa.gl`, X-Accel-Redirect, ARC-008)
- `GET https://usercontent.zaa.gl/d/<ticket>` → Nginx proxies to api, which checks the ticket (MAC, expiry, single use,
  burn/expiry/takedown/scan state) and answers with `X-Accel-Redirect` to an `internal` location
  (`alias /srv/zaagl/storage/objects/` and the derived areas), `Content-Type` from the DB allow-list (never sniffed),
  `Content-Disposition`, `X-Accel-Buffering: no`. Range/`If-Range` are handled natively by Nginx.
- **Per-response cap `Content:MaxResponseBytes`** (PD-38, proposed 90,000,000): an object above the cap is served only
  as `206` parts below the cap; a request without an acceptable Range gets `416 range-required` (ARC-008 §5). Every
  request and response on every host therefore stays under Cloudflare's per-request limit.
- **CORS preflight:** `OPTIONS /d/<ticket>` is answered by Nginx itself with `204`, `Access-Control-Allow-Origin:
  https://app.zaa.gl`, `Allow-Methods: GET, HEAD`, `Allow-Headers: Range`, `Max-Age: 600`; it never reaches the API
  (ARC-008 §9). It is needed by the viewers' cross-origin `fetch` with `Range` (react-pdf, chunked saves). WAF rule
  C3 allows `GET`, `HEAD`, `OPTIONS` on this host (ops-001 §5).
- Cloudflare caches nothing on this host (ops-001 §3: Cache Rule 1 bypass, origin `Cache-Control: private, no-store`).
- Burn-after-download semantics with Range parts: ARC-008 §6 (claim on the first counted `GET`, grace window for parts).
- Tickets are never logged in clear: `map` redaction in `log_format` (ARC-008 §10, OPS-022).

### 3.4 TLS, origin protection, headers (detail in ops-001 §2 and §5)
- Cloudflare **Full (strict)**, Cloudflare **Origin CA** certificate for `zaa.gl, *.zaa.gl` (key as a Docker secret in
  `proxy`), **Authenticated Origin Pulls** (`ssl_verify_client on`), Min TLS 1.2, 0-RTT off.
- nftables: 443 accepted **only from Cloudflare ranges** (refreshed by a timer); 80 closed; SSH only from the admin
  allow-list. With no DNS-only host left, no second public IPv4 is needed (it returns only if PD-52 picks option (b)).
- `set_real_ip_from <CF ranges>; real_ip_header CF-Connecting-IP;` in every server block; default server returns `444`.
- All hosts: HSTS (`includeSubDomains` only after every host passes; preload off), `X-Content-Type-Options: nosniff`,
  `server_tokens off`. `usercontent.zaa.gl`: the ARC-008 §10 header set (sandbox CSP, `X-Robots-Tag: noindex`,
  `Cross-Origin-Resource-Policy: cross-origin`, `Referrer-Policy: no-referrer`, attachment outside the inline allow-list,
  `Cache-Control: private, no-store`).
- `app.zaa.gl` CSP adds `img-src/media-src/connect-src https://usercontent.zaa.gl` (frontend owns the header).
- Edge limits: one Cloudflare rate-limit rule R1 on `/api/auth/*` (50 per 10 s per IP [EST]); Nginx `limit_req` on
  `api.zaa.gl` 20 r/s per IP burst 40 and on `zaa.gl` 50 r/s per IP [EST]; the application limiters stay the real controls.

---

## 4. ClamAV, media workers, isolation

- **ClamAV**: `clamav/clamav` pinned version, own container on `backend` + `egress` (freshclam needs internet), `mem_limit 4g` (signatures ≈ 1–1.5 GB, doubled during reload unless `ConcurrentDatabaseReload no` [EST]), `cpus 2`. Scan by path (`SCAN /srv/zaagl/storage/temp/...` over TCP, read-only mount) instead of nClam `INSTREAM`, which would push 5 GB through a socket and is capped by `StreamMaxLength`. `clamd.conf`: `MaxFileSize`/`MaxScanSize` ≈ 4000M (ClamAV cannot scan above ~4 GB; verify against the pinned version), `MaxRecursion 16`, `MaxFiles 10000`, `AlertExceedsMax yes`, `MaxThreads 2`. Files above the scan limit → state `scan-limit-exceeded` with a policy decision (Q5). ClamAV down ⇒ uploads stay `pending-scan` (never published unscanned), alert after 10 min.
- **Worker** (`Zaagl.Worker`, Wolverine): one image with FFmpeg + libvips (NetVips), `backend` network only (**no internet**, kills SSRF via media), `read_only: true` + tmpfs `/tmp` 512 MB, scratch on `temp/`. Limits [EST for a 16 vCPU / 64 GB host]: `cpus: 4`, `mem_limit: 8g`, `cpu_shares: 256` (api/proxy/postgres 1024), `blkio_config.weight: 100`, `pids_limit: 512`. Queues: scan 2, image 4, video 1 (+1 for Pro), each with a timeout (vips 2 min, FFmpeg 30 min) and a dead-letter state.
- FFmpeg hardening: `-protocol_whitelist file`, no `concat`/HLS/playlist demuxers on untrusted input, `-threads 2`, `nice -n 10`. "Original quality": originals are never re-encoded; screen recordings get at most a `-c copy -movflags +faststart` remux copy and a poster/preview rendition when the codec isn't browser-playable.
- libvips: `VIPS_CONCURRENCY=2`, decompression-bomb guard (max pixels ~ 200 MP [EST]), `fail_on=error`, operation cache off.
- Result: worst case worker+ClamAV use 6 of 16 cores and 12 of 64 GB; api, proxy, postgres keep the rest. Verified only by OPS-028 load test **[UNVERIFIED-DOCKER]**.

---

## 5. CI/CD (GitHub Actions)

| Pipeline | Where | Contents |
|---|---|---|
| backend-ci | `Zaagl-Backend/.github/workflows/backend-ci.yml` | `setup-dotnet 10`; service `postgres:18` on `5433:5432` (A18) + apply `deploy/init-db/*.sql` with `psql`; `dotnet restore`, `dotnet build -c Release -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test` (TRX + coverage), `dotnet list package --vulnerable` |
| migrations-check | same repo | per DbContext `dotnet ef migrations has-pending-model-changes`; build EF bundles; run the `migrate` entrypoint twice against an empty DB (idempotent); later (spec §3) previous release's tests against new schema |
| images | same repo, on `main` + tags | buildx api/coordinator/worker → GHCR, tag `sha-<git sha>` (coordinator: input hash per spec §3), SBOM, Trivy (fail on critical), cache `type=gha` |
| frontend-ci | `Zaagl-Frontend/.github/workflows/frontend-ci.yml` | `npm ci`, `npm run lint`, `tsc --noEmit`, `next build`; on `main` build `web` image (needs standalone output, A20) |
| desktop | repo TBD (Q7) | `tauri-apps/tauri-action`, matrix `windows-latest` (x64, arm64), `macos-latest` (universal); PR = unsigned artifacts; tag = signed + notarised + Tauri updater signature |
| cli | repo TBD (Q7) | Native AOT can't cross-compile across OSes → matrix win-x64/arm64, linux-x64/arm64, osx-x64/arm64; strip, SHA-256 sums, signatures; also `dotnet tool` package as fallback |
| deploy | backend repo, `workflow_dispatch` + tag | GitHub Environments `staging` (auto) / `production` (required reviewer) → SSH to VPS as `deploy` (forced-command key) → `deploy/scripts/deploy.sh <tag>` |

Signing: **macOS** Developer ID Application cert + `notarytool` with an App Store Connect API key, staple. **Windows**: CA rules since 2023 require the key on an HSM, so a PFX in a GitHub secret no longer works; use Azure Trusted/Artifact Signing (OIDC, no key export) or a cloud-HSM EV/OV (DigiCert KeyLocker, SSL.com eSigner). EV no longer guarantees instant SmartScreen reputation: plan for reputation build-up (PRD risk "Desktop app OS permissions"). **CLI**: Authenticode for `.exe`, codesign+notarise mac binaries in a zip, plus minisign/cosign signature on the checksum file.
Distribution: GitHub Releases in a public `zaagl-releases` repo (private-repo assets need auth), Tauri updater `latest.json` there, then winget, Homebrew tap/cask and Scoop manifests. Do **not** serve installers from `zaa.gl` (spec §1.2: codes only); a download page on `www`/`app` links to GitHub.

Deploy and rollback: images immutable by SHA; `deploy.sh` = pull → `run --rm migrator` (expand-only migrations, spec §3) → `up -d api worker web` → wait `/health/ready` (internal) → smoke (`HEAD zaa.gl/<canary-code>`, tus create+upload 1 MB, download via X-Accel) → record tag. `rollback.sh` = previous tag, no migration rollback (expand-only makes the old image compatible). Nginx config changes: `nginx -t` then `reload`. In-flight downloads survive api restarts (Nginx streams), tus resumes. Blue/green api is P3.

---

## 6. Secrets, observability, backups

**Secrets**: compose file-based `secrets:` from `/srv/zaagl/secrets` (0440 root:zaagl), read by a key-per-file config provider (backend change). New for Phase 1: `TRANSFER_GRANT_KEY` (HMAC for upload/download grants, ≥ 32 B), `CF_DNS_API_TOKEN` (DNS-01, one zone), `PGBACKREST_REPO_KEY` + repo credentials, `RESTIC_PASSWORD` + repo credentials (write/append-only), `ALERT_WEBHOOK_URL`. CI-only: Apple cert + notary key, Windows signing (OIDC), Tauri updater private key, deploy SSH key, minisign key. Escrow `RESTIC_PASSWORD` and `PGBACKREST_REPO_KEY` off-host next to `SCRAMBLE_KEY_V1`: without them the backups are unreadable.

**Metrics** (OpenTelemetry → Prometheus; Grafana optional per TS §20): node_exporter (disk bytes/inodes per mount, net rx/tx, load), nginx exporter + JSON access log, postgres exporter, app counters: uploads started/completed/failed by reason, tus bytes, session rejections (507), scan outcomes (clean/infected/error/limit-exceeded), scan queue depth + oldest age, media job duration/failures, expiry/burn lag, storage bytes by tier, quarantine count, freshclam signature age, redirect p99. External: uptime checks on `zaa.gl/<canary>`, `app`, `api`, `dl.<ucd>`; dead-man pings (healthchecks.io-style) from backup jobs.

| Alert | Condition [EST thresholds] | Severity |
|---|---|---|
| Storage disk | > 70 % warn, > 80 % page, inodes > 80 % | page |
| Temp quota | > 80 % of project quota | warn |
| Postgres disk / WAL archive | > 85 % / `failed_count` increase or last archive > 5 min | page |
| Scan failures | ClamAV unreachable 10 min, error rate > 5 %/15 min, queue age > 15 min, signatures > 48 h old | page |
| Upload errors | 5xx or failed tus sessions > 5 %/15 min; 507 rejections > 0 | page / warn |
| Expiry/burn lag | expired items still on disk > 15 min | page (privacy) |
| Redirect health | p99 > 50 ms (PRD E0.1) for 10 min or error rate > 1 % | page |
| Bandwidth | monthly egress > 70 % of plan allowance | warn |
| Backups | no restic or pgBackRest success in 26 h; `restic check` failure | page |
| TLS | cert expiry < 14 days | warn |

**Logs**: Docker `json-file` `max-size 50m max-file 5` everywhere; Serilog compact JSON; Nginx JSON with redacted grants; retention 14 days on host [EST], optional Loki later. No file contents or filenames in error logs beyond IDs.

**Backups**: PostgreSQL: pgBackRest in a custom postgres image, `archive_mode=on`, `archive_timeout=60s` (spec §3), repo on EU S3-compatible storage, weekly full + daily diff, encrypted. Files: **restic** (S3 backend, no SSH target needed; Borg is fine if the target is a Storage Box), every 6 h, scope `objects/` only (thumbnails/previews regenerable; temp/quarantine excluded; `transfers/` excluded if Q3 is accepted), `forget --keep-hourly 24 --keep-daily 14 --keep-weekly 8`, weekly `check --read-data-subset=5%`, credentials on the VPS cannot delete (bucket object-lock or rest-server `--append-only`); prune runs from a separate trusted machine. Erasure note: burned/taken-down files survive in backups up to the retention window: legal wording needed (Q3).
DB↔files consistency after restore: reconciler job compares DB rows (path + SHA-256) to disk; orphans → quarantine, missing → item state `lost`, report counts.
**Restore drill** (pre-launch gate, then quarterly, extends `docs/RUNBOOK.md`): scratch VPS → pgBackRest PITR → restic restore `objects/` → RUNBOOK DB steps 3–6 → reconciler → verify 50 random files' SHA-256 and a download through X-Accel → record RPO (DB ≤ ~1 min, files ≤ 6 h [EST]) and RTO (1 TB at ~100 MB/s ≈ 3 h transfer, target ≤ 8 h [EST]).

---

## 7. Tasks

Format: **ID · title · priority** — description | files | deps | acceptance | verification.

- **OPS-001 · Buy and wire the user-content domain + DNS plan · P0** — buy `<ucd>`, CF zone, `cdn` proxied, `dl` DNS-only (second IPv4 if available), CAA records, ADR in `docs/DECISIONS.md`. | `docs/DECISIONS.md` | Q2 | domain resolves, ADR merged | `dig`; **[CRED]**.
- **OPS-002 · Storage policy & capacity sign-off · P0** — PO decides Pro permanent vs ephemeral, per-account quotas, retention; update §2.3 model. | this doc, `docs/DECISIONS.md` | Q1, Q3 | numbers agreed and written into Billing `PlanLimits` contract | review only.
- **OPS-003 · Image hygiene · P0** — add `.dockerignore`; fixed UID/GID 10001; pin base images by digest; replace `curl` healthcheck with a tiny probe or keep and document. | `.dockerignore`, `src/Zaagl.Api/Dockerfile`, `src/Zaagl.Coordinator/Dockerfile` | – | context < 50 MB, `id zaagl` = 10001 | CI build (OPS-015); **[UNVERIFIED-DOCKER]** locally.
- **OPS-004 · backend-ci workflow · P0** — §5 row 1. | `.github/workflows/backend-ci.yml` | – | green on PR; failing test fails the job | GitHub runner (no local Docker needed).
- **OPS-005 · Migrations check · P0** — §5 row 2. | `.github/workflows/backend-ci.yml` | OPS-004 | pending model change fails CI; migrate twice exits 0 | GitHub runner.
- **OPS-006 · frontend-ci workflow · P0** — lint, typecheck, build. | `Zaagl-Frontend/.github/workflows/frontend-ci.yml` | – | green on PR | GitHub runner.
- **OPS-007 · Production compose · P0** — `deploy/docker-compose.yml` (+ dev override with mailpit, published ports) per spec §3: `edge`/`backend internal`/`egress`, fixed IPs, no published ports but proxy, secrets, limits, logging, `read_only`; Valkey per OPS-029. Backend sets `KnownProxies` to proxy+web IPs and adds a content-host class to `HostRouting`. | `deploy/docker-compose.yml`, `deploy/docker-compose.override.yml`, `deploy/.env.example` | OPS-003, OPS-014 | `docker compose config` valid; `ss -tlnp` on host shows only 80/443/22 | `config` lint in CI; runtime **[UNVERIFIED-DOCKER]**.
- **OPS-008 · Worker image + service · P0** — Dockerfile with FFmpeg/libvips, no network, limits from §4. | `src/Zaagl.Worker/Dockerfile`, compose | OPS-007, backend worker | `ffmpeg -version`, vips load test image in container | CI build; **[UNVERIFIED-DOCKER]**.
- **OPS-009 · ClamAV service · P0** — container, `clamd.conf`, freshclam, path scanning, EICAR smoke. | `deploy/clamav/clamd.conf`, compose | OPS-007 | EICAR → infected; 4 GB file handled per policy | CI job with service container (possible); **[UNVERIFIED-DOCKER]** locally.
- **OPS-010 · Nginx configuration · P0** — §3 complete: server blocks, real-IP, AOP, tus, X-Accel internal locations, headers, limits, health deny. | `deploy/nginx/nginx.conf`, `deploy/nginx/conf.d/*.conf`, `deploy/nginx/snippets/*` | OPS-001, OPS-007 | `nginx -t`; 5 GB upload resumes after kill; range request 206; `/health` 404 externally; CSP present on `<ucd>` | `nginx -t` in CI container; end-to-end needs VPS **[CRED]**.
- **OPS-011 · Host provisioning · P0** — Ubuntu 24.04, data disk XFS `prjquota`, mounts, dirs/perms §2.2, nftables, SSH hardening, unattended-upgrades, Docker log defaults. | `deploy/host/provision.sh` (or Ansible) | OPS-002 | `xfs_quota -x -c report` shows quotas; nginx cannot read `temp/` | on VPS **[CRED]**.
- **OPS-012 · Disk admission & thresholds contract · P0** — spec the §2.4 contract with backend (507, `Upload-Length`, in-flight reservation, temp janitor). | this doc → `docs/RUNBOOK.md` | OPS-002 | backend tests cover 507 path | backend test with fake free-space provider.
- **OPS-013 · TLS automation · P1** — Origin CA certs for proxied hosts, LE DNS-01 for `dl.<ucd>`, renewal + reload, expiry alert. | `deploy/nginx/`, `deploy/host/` | OPS-001, OPS-010 | SSL Labs A; renewal dry-run ok | VPS **[CRED]**.
- **OPS-014 · Secrets for Phase 1 · P0** — extend README with §6 secrets; key-per-file provider (backend); host dir perms. | `deploy/secrets/README.md` | – | no secret in `docker inspect` env | review + `docker inspect` **[UNVERIFIED-DOCKER]**.
- **OPS-015 · Image build/push · P1** — GHCR, SHA tags, coordinator input hash, SBOM, Trivy. | `.github/workflows/images.yml` | OPS-003, OPS-004 | images pullable by SHA; criticals fail | GitHub runner.
- **OPS-016 · Deploy + rollback · P1** — scripts and workflow from §5, Environments with reviewers. | `deploy/scripts/deploy.sh`, `rollback.sh`, `.github/workflows/deploy.yml` | OPS-007, OPS-015 | rollback to previous tag < 5 min on staging | staging **[CRED]**.
- **OPS-017 · PostgreSQL backups + WAL archiving · P0** — custom postgres image with pgBackRest, archive to EU S3, schedules; flip `RequireArchiveConfirmation=true` in staging/prod. | `deploy/postgres/Dockerfile`, `pgbackrest.conf`, compose | OPS-007 | `pgbackrest check` ok; `pg_stat_archiver` advancing | **[CRED]** + **[UNVERIFIED-DOCKER]**.
- **OPS-018 · File backups (restic) · P0** — scope/exclusions/schedule/retention/append-only from §6, systemd timers, dead-man ping. | `deploy/backup/restic-*.sh`, `deploy/host/systemd/*` | OPS-011, Q3 | snapshot every 6 h; VPS creds cannot delete | VPS **[CRED]**.
- **OPS-019 · Reconciler + restore drill #1 · P1 (launch gate)** — runbook section, reconciler spec (backend implements), first drill with measured RTO/RPO. | `docs/RUNBOOK.md` | OPS-017, OPS-018 | drill report with times and checksum results | scratch VPS **[CRED]**.
- **OPS-020 · Metrics stack · P1** — OTel in api/worker (backend), exporters, Prometheus, Alertmanager, optional Grafana, external uptime. | `deploy/monitoring/*`, compose profile `monitoring` | OPS-007 | dashboards show §6 metrics | **[UNVERIFIED-DOCKER]**.
- **OPS-021 · Alert rules · P1** — §6 table as Prometheus rules + routing. | `deploy/monitoring/alerts.yml` | OPS-020 | `promtool check rules`; synthetic fire test | `promtool` runs without Docker.
- **OPS-022 · Logging · P1** — json-file rotation, Serilog JSON, Nginx JSON with redaction, retention. | compose, `deploy/nginx/`, `appsettings.json` (backend) | OPS-007, OPS-010 | no grant token in logs (grep test) | **[UNVERIFIED-DOCKER]**.
- **OPS-023 · Desktop CI (unsigned) · P1** — Tauri matrix, artifacts on PRs. | `<desktop repo>/.github/workflows/desktop.yml` | Q7 | installers produced for win x64/arm64 + mac universal | GitHub runner.
- **OPS-024 · Desktop signing/notarisation/updater · P1** — §5 signing. | same | OPS-023 | no Gatekeeper block; signature verified by `signtool`/`spctl` | **[CRED]** (Apple Developer ID, Windows signing account).
- **OPS-025 · CLI CI + signing · P1** — AOT matrix, sums, signatures. | `<cli repo>/.github/workflows/cli.yml` | Q7 | 6 binaries + SHA256SUMS + signature per tag | GitHub runner; signing **[CRED]**.
- **OPS-026 · Release distribution · P2** — `zaagl-releases` repo, updater feed, winget/Homebrew/Scoop. | release repo | OPS-024, OPS-025 | `winget install`/`brew install` work; updater upgrades N-1 → N | manual on clean VMs.
- **OPS-027 · Staging environment · P1** — separate small VPS (preferred) or isolated stack, own secrets, access gate, noindex, Mailpit. | compose override `staging` | OPS-007, OPS-016 | spec §3 staging rules hold | **[CRED]**.
- **OPS-028 · Load/isolation test · P2** — k6: 20 concurrent 5 GB tus uploads + 3 video transcodes while measuring redirect p99. | `tests/load/*.js` | OPS-008..010 | redirect p99 < 50 ms during load | staging **[CRED]**.
- **OPS-029 · Valkey decision · P2** — ADR: keep (internal, `requirepass` via secret, `maxmemory` + `allkeys-lru`, no persistence) or drop and make it optional in `Program.cs`. | `docs/DECISIONS.md`, compose | Q8 | ADR merged; no published 6379 | review.
- **OPS-030 · Bandwidth controls · P2** — per-tier `X-Accel-Limit-Rate`, monthly egress tracking vs plan. | Nginx, backend header | OPS-010 | Free download throttled to configured rate | staging.
- **OPS-031 · Zero-downtime api deploy · P3** — blue/green upstream switch. | `deploy/nginx/`, `deploy.sh` | OPS-016 | 0 failed redirects during deploy | staging.
- **OPS-032 · Runbook & scaling docs for Phase 1 · P1** — disk-full, ClamAV down, scan backlog, file takedown, file restore, moving storage to S3 via `IFileStorage`. | `docs/RUNBOOK.md`, `docs/SCALING.md` | OPS-012, OPS-019 | sections exist and are referenced by alerts | review.

Critical path to a Phase 1 launch: OPS-002 → OPS-011 → OPS-007/008/009/010 → OPS-017/018 → OPS-019 drill. OPS-004/005/006 can start immediately and are the only tasks fully verifiable without Docker or credentials.

---

## 8. Open questions

1. **Pro storage**: PRD says "permanent storage" for 5 GB files; this brief says ephemeral transfer. Which, and what per-account total quota? (drives disk size, backups, cost)
2. User-content domain name, and whether thumbnails/inline images may go through Cloudflare (`cdn.<ucd>`) under its CDN terms, or everything through DNS-only `dl.<ucd>`.
3. May ephemeral/burn-after-download files be excluded from backups (lost on disaster) and how long may deleted files persist in backups (GDPR/legal wording)?
4. Burn semantics with resumable/Range downloads: one grant window (proposal §3.3) or strict single complete transfer?
5. Files above ClamAV's ~4 GB limit: reject, accept as "unscanned" with a warning, or Pro-only with the uploader's attestation?
6. VPS provider and plan: monthly traffic allowance, second IPv4 availability, attachable block volume vs dedicated server disks, EU region.
7. Where do the Tauri desktop app and .NET CLI live (new repos or folders in Zaagl-Backend/Frontend)? Public release repo acceptable?
8. Valkey: keep per tech-stack doc or follow spec §9.6 (Redis only later)? Today the API won't start without it.
9. Postgres 17 (spec) or 18 (compose and tech-stack)? Needs an ADR before pgBackRest image work.
10. Windows signing route: Azure Trusted/Artifact Signing (needs eligible org verification) or a cloud-HSM EV certificate? Who owns the Apple Developer account?
11. Staging: separate VPS budget approved, or co-hosted with stricter isolation?
12. Alert destination (email, Telegram, Slack, PagerDuty-like) and who is on call?
13. Are viewer pages for files on `app.zaa.gl` (cookies present) only rendering, with all bytes from `<ucd>`? Confirm with frontend/backend so no user bytes are served from a cookie-bearing origin.
14. Guest (anonymous) uploads in Phase 1: allowed? If so, limits and Turnstile on `dl.<ucd>` session creation.
