# Stage 1 review, round 3: DevOps (OPS-001 hosts, DNS and Cloudflare edge)

Reviewer: zaagl-head. Date: 2026-11-09. Files: `docs/phase1-implementation/design/ops-001-hosts-and-dns.md` (new) and the RUNBOOK section "Cloudflare edge: settings that must stay off". Nothing was applied to any account. Every step is correctly marked BLOCKED or VERIFY, and the evidence labels are honest ([DOC]/[KNOWN]/[EST]/[VERIFY]).

## Verdict: ACCEPT WITH NOTES (owner: devops, plus the architect for one item)

## Against the approved decisions

| Decision | Plan | Result |
|---|---|---|
| PD-03 subdomain, never a cookie on it or on `.zaa.gl` | `usercontent.zaa.gl`, no domain bought. §4 removes every Cloudflare feature that sets cookies (Bot Fight Mode off, no challenge actions, Always Online off, Turnstile pre-clearance off). A cookie probe runs every 15 min. | OK |
| PD-12 uploads on `api.zaa.gl`, ≤ 90 MB chunks | Nginx `client_max_body_size 90000000` on `/api/uploads` only, 2 MB elsewhere. | OK |
| PD-38 `usercontent` proxied, delivery in chunks | Proxied. Response cap `Content:MaxResponseBytes`. Caches nothing (all ticket URLs are single-use, so the hit rate is zero, and takedown, burn and expiry take effect immediately). | OK. Caching nothing is the safe reading of addendum §4.4. |
| PD-46 cookies on `api` only for the sign-in handshake | Host-only, ≤ 10 min, probe alerts on any other `api` cookie. | OK |
| PD-52 terms decision deferred | Blocker B1 lists options (a) to (d) and step 12 records the decision before Stage 7. | OK, matches PD-52 |

Good additional controls:
- No grey-cloud record ever points at the VPS.
- No wildcard record.
- Origin firewall accepts only Cloudflare ranges.
- Authenticated Origin Pulls.
- 0-RTT off.
- HSTS `includeSubDomains` only after every host passes; preload off.
- Staging on its own VPS with its own origin key.

## Notes (fix in the document; no user input needed)

1. **WAF rule C3 vs ARC-008 / ADR-0023.**
   - C3 blocks every method except GET/HEAD on `usercontent.zaa.gl`.
   - ARC-008 allows OPTIONS, and the viewers' cross-origin `fetch` with `Range` needs a CORS preflight.
   - Agree with the architect, then add OPTIONS to C3 (the document already anticipates this).
2. **Edge rate-limit rule R1 (`/api/auth/*`, 50 per 10 s per IP).** Safe for the web app, because the BFF reaches the API on the internal address (`ZAAGL_INTERNAL_API_URL`), not through Cloudflare. The document should say so explicitly. CLI device-flow polling (`/api/auth/device/token`, 5 s interval) from users behind one office or CGNAT IP also counts against it; mention that.
3. **DEVOPS_PLAN.md §3.1 to §3.4** still describe the old two-domain layout. DevOps owns that file and must update it, or mark it superseded by ops-001.
4. **Worker deployment (from BE-013).** `src/Zaagl.Worker` has no Dockerfile and no service in `docker-compose.yml`. It has a liveness file for a healthcheck. Add both under OPS (not urgent: no job kinds run until Stage 2/3).
5. Under Caching, "I decided against it" (public previews not cached) is in scope for DevOps and consistent with the addendum. The document correctly says that changing it needs the product owner.

## Questions for the user raised by this plan (all "later", none blocks Stage 2)

- U-10 staging hostnames: buy Cloudflare Advanced Certificate Manager (about US$10 a month) or rename staging to one level (`staging-api.zaa.gl`, …, a spec §1.2 change).
- U-11 `www.zaa.gl` → 301 to `app.zaa.gl`: yes or no.
- U-12 procurement and accounts (with PD-27):
  - email provider for `notify.zaa.gl`
  - a security contact mailbox (`security@zaa.gl` needs a mail provider MX)
  - the spare domain
  - the admin IP addresses for the staging gate
  - Cloudflare, registrar and Google Search Console access

## Process

- Stayed in its own files: the new design note and its own RUNBOOK section. The backend's RUNBOOK lines (KnownProxies) are separate.
- No commits, branches or worktrees.
- Nothing was run (no Docker, no VPS, no account), and the document says so.
