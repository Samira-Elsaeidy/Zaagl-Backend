# OPS-001 Hosts, DNS and Cloudflare edge plan

| | |
|---|---|
| Task | OPS-001 (DEVOPS_PLAN.md §7), re-scoped by PD-03, PD-12, PD-38 and PD-46 (DECISIONS.md, 2026-11-09) |
| Owner | DevOps |
| Status | DRAFT for review. Nothing here has been applied to any Cloudflare account or server. |
| Rules it implements | spec v9.4 §1.2 (hosts); addendum §4.1, §4.1.1, §4.1.2, §4.2, §4.4, §6.1, §20 |
| Date | 2026-11-09 |

**What changed from DEVOPS_PLAN.md §3 and §7:** no domain is bought. `cdn.<ucd>` and `dl.<ucd>` are replaced by a single
proxied host `usercontent.zaa.gl` (PD-03, PD-38). Uploads go to `api.zaa.gl` in chunks of at most 90,000,000 bytes
(PD-12), not to a DNS-only host with 50 MB chunks. With no DNS-only host left, the origin firewall becomes simpler:
port 443 is accepted only from Cloudflare ranges. DEVOPS_PLAN.md §3.1 to §3.4 were rewritten to this layout on
2026-11-09 (Head review stage1-round3-devops, note 3).

**Evidence labels used below:**
- **[DOC 2026-11-09]**: read today on developers.cloudflare.com or cloudflare.com.
- **[KNOWN]**: Cloudflare behaviour I know well but did not re-read today.
- **[EST]**: an estimate.
- **[VERIFY]**: has to be checked in the dashboard or by a test.

Nothing in this plan has been run. Docker cannot run on this machine, there is no VPS yet and I have no account access.

---

## 0. Blockers (read first)

| # | Blocker | Why it blocks | What is needed | From |
|---|---|---|---|---|
| B1 | **Cloudflare terms vs PD-38.** The *Service-Specific Terms, Application Services*, "Content Delivery Network (Free, Pro, or Business)" section (page last updated 2026-09-28) say that non-Enterprise customers must use Paid Services (Developer Platform, Images, Stream) to serve video and other large files through the CDN. They also say Cloudflare may restrict the CDN, or end users' access to some resources, if it is used for video or a disproportionate share of pictures, audio or large files without those services. **[DOC 2026-11-09]** | Under PD-38, `usercontent.zaa.gl` serves downloads, video and archives through the Free CDN. Chunking keeps each request under the size limit, but the terms restrict the *type of use*, not the request size. This is addendum §20.1. The risk is **real, not theoretical**: the likely outcome is restricted delivery on the whole `zaa.gl` zone, which includes the redirects. | A decision from the product owner before Stage 7 (launch). Options: (a) keep PD-38 and accept the risk of the zone being restricted; (b) make `usercontent.zaa.gl` DNS-only on a **second public IPv4** (origin hiding is lost for that IP only; TLS from Let's Encrypt with DNS-01); (c) move video and large files to a paid Cloudflare service (Stream or R2; this changes the local-disk storage design); (d) put the delivery host on a separate zone/domain (this reopens PD-03). The chunking and response cap in this plan stay valid under (a), (b) and (c). | Product owner (re-decision under §20.1) |
| B2 | **Staging hostnames are two levels deep.** Universal SSL (Free) covers `zaa.gl` and `*.zaa.gl` only. It does not cover `api.staging.zaa.gl`, `app.staging.zaa.gl` or `usercontent.staging.zaa.gl`. **[KNOWN]** | Those proxied hosts would fail the edge TLS handshake. | Either **buy Advanced Certificate Manager** (about US$10 a month per zone [EST, check the price]), which keeps the spec §1.2 names, or have the product owner rename staging to one level (`staging.zaa.gl`, `staging-api.zaa.gl`, `staging-app.zaa.gl`, `staging-usercontent.zaa.gl`), which is a spec §1.2 change. | User (purchase) or product owner (spec change) |
| B3 | Account access: the Cloudflare account, the `zaa.gl` zone, the registrar (nameservers, DNSSEC DS record) and the VPS IPv4/IPv6. | Every step in §7. | The user does the checklist, or grants a scoped API token (Zone DNS Edit, Zone Settings Edit, Zone WAF Edit, SSL and Certificates Edit, zone `zaa.gl` only). | User |
| B4 | Email provider for `notify.zaa.gl` is not chosen. | The SPF include, DKIM selector names and bounce CNAME depend on it. | Provider choice (it belongs with PD-27 procurement). | User / product owner |
| B5 | Staging VPS (PD-27, OPEN). | The staging A records need an IP. | Procurement. | User |

---

## 1. Hostname and DNS record plan

`<ORIGIN_V4>` / `<ORIGIN_V6>` are the production VPS addresses. `<STAGING_V4>` is the staging VPS address (B5).
All proxied records use Cloudflare's IPs at the edge, so the origin IP never appears in public DNS.

| Hostname | Record | Value | Proxy | Serves (spec §1.2 / addendum §4.1) | Cookies allowed |
|---|---|---|---|---|---|
| `zaa.gl` | A (+ AAAA if the VPS has IPv6) | `<ORIGIN_V4>` / `<ORIGIN_V6>` | **Proxied** | `GET/HEAD /{code}`, 404/410 pages, `/.well-known/security.txt` | **none** |
| `api.zaa.gl` | A (+ AAAA) | origin | **Proxied** | `/api/*`, tus uploads, `/account/*` pages, SignalR hub | only the sign-in handshake cookies of §4.1.2 (PD-46): host-only, at most 10 minutes |
| `app.zaa.gl` | A (+ AAAA) | origin | **Proxied** | web app, BFF, viewers `/p /i /f`, `/activate` | host-only `__Host-` only |
| `usercontent.zaa.gl` | A (+ AAAA) | origin | **Proxied** (PD-38, see B1) | user bytes by ticket | **none** |
| `www.zaa.gl` | A | origin (never served: a Cloudflare redirect rule answers first) | **Proxied** | 301 to `https://app.zaa.gl/` **(PROPOSED; www is not in spec §1.2, product owner to confirm)** | none |
| `notify.zaa.gl` | **no A/AAAA**. TXT/CNAME only, see §1.1 | provider | DNS-only (mail records cannot be proxied) | email sending domain | n/a |
| `staging.zaa.gl` | A | `<STAGING_V4>` | **Proxied** | staging short host | none |
| `api.staging.zaa.gl` | A | `<STAGING_V4>` | **Proxied** (needs B2) | staging API | as production |
| `app.staging.zaa.gl` | A | `<STAGING_V4>` | **Proxied** (needs B2) | staging app | as production |
| `usercontent.staging.zaa.gl` | A | `<STAGING_V4>` | **Proxied** (needs B2) | staging user bytes (needed to test §4.2 and §4.4) | none |

Rules:
1. **No DNS-only A/AAAA record may ever point at the production VPS.** This means no `direct.`, no `ssh.` and no MX
   pointing at it. One grey-cloud record would publish the origin IP and make every proxied protection pointless. SSH is
   reached by IP from the admin allow-list, never by a hostname in this zone.
2. **No wildcard record** (`*.zaa.gl`). An unknown name must give NXDOMAIN, not reach the origin. Nginx's default server
   also returns `444` for an unknown `Host`.
3. PostgreSQL, the coordinator, ClamAV, the worker and `/health/*` have **no DNS name and no published port**. Only the
   `proxy` container publishes 443 (and 80, if used at all). `/health/*` is also blocked at the edge (§4, rule C1).
4. Staging runs on a **separate VPS** with its own origin certificate and secrets. A staging compromise must not hold
   the production origin key.
5. Residual risk, noted rather than fixed: the staging hosts are same-site with `app.zaa.gl`. The controls in
   addendum §4.2 (host-only `__Host-` cookies, CSRF on every state change, no `Domain=zaa.gl` cookie) also cover a
   compromised staging host. Spec §1.2 chose this layout.
6. Spare domain (spec §1.2): registered and parked in its **own** Cloudflare zone with no records that point at
   any origin. **BLOCKED: purchase**, when the user decides.

### 1.1 Email records (`notify.zaa.gl`, spec §1.2 and §7.10). Provider-specific values are BLOCKED (B4)

| Name | Type | Value |
|---|---|---|
| `notify.zaa.gl` | TXT | `v=spf1 include:<provider-spf> -all` |
| `<selector>._domainkey.notify.zaa.gl` | CNAME or TXT | as the provider gives it (DNS-only) |
| `<bounce>.notify.zaa.gl` | CNAME / MX | provider's Return-Path host, if the provider uses a custom bounce domain |
| `_dmarc.notify.zaa.gl` | TXT | `v=DMARC1; p=quarantine; adkim=s; aspf=s; rua=mailto:<reports>` (spec minimum). Move to `p=reject` after 2 to 4 weeks of clean reports. |
| `zaa.gl` | TXT | `v=spf1 -all` (the apex never sends mail) |
| `_dmarc.zaa.gl` | TXT | `v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:<reports>`. This covers `api`, `app`, `usercontent`, `www` and staging. `notify` has its own record above. |
| `zaa.gl` | MX | `0 .` (null MX, RFC 7505) **only if** no address `@zaa.gl` receives mail. **OPEN:** `security.txt` needs a contact. If that contact is `security@zaa.gl`, use a provider MX instead (never the VPS, rule 1). |

Origin IP leak through mail: send only through the provider's HTTP API, or through SMTP from the API container.
Some providers copy the submitting client IP into a `Received:` header. Check one delivered message's headers in
staging. If the VPS IP shows up, use the provider's HTTP API. **[VERIFY]**

### 1.2 CAA

All public certificates for this zone are Cloudflare edge certificates. Origin CA certificates are not publicly
trusted and are not governed by CAA.

| Name | Type | Value |
|---|---|---|
| `zaa.gl` | CAA | `0 issue "letsencrypt.org"` |
| `zaa.gl` | CAA | `0 issue "pki.goog"` |
| `zaa.gl` | CAA | `0 issue "ssl.com"` |
| `zaa.gl` | CAA | `0 issuewild "letsencrypt.org"`, `0 issuewild "pki.goog"`, `0 issuewild "ssl.com"` |
| `zaa.gl` | CAA | `0 iodef "mailto:<security contact>"` |

Once any CAA record exists, Cloudflare adds the CAs that Universal SSL uses itself. **[KNOWN]** After saving, run
`dig CAA zaa.gl +short` and compare with the CA list under *SSL/TLS > Edge Certificates*. If Cloudflare names a CA that
is not listed here, add it. Never set `issuewild ";"`, because the Universal SSL certificate is a wildcard. **[VERIFY]**
If B1 is resolved with option (b), the DNS-only host needs Let's Encrypt, which is already allowed.

### 1.3 DNSSEC
Enable it in *DNS > Settings*, then add the DS record at the registrar. **BLOCKED: registrar access (B3).** Check with
`dig +dnssec zaa.gl` and an online DNSSEC analyser.

---

## 2. TLS

| Setting | Value | Why |
|---|---|---|
| SSL/TLS encryption mode | **Full (strict)** | The edge validates the origin certificate. |
| Origin certificates | Cloudflare **Origin CA**, RSA 2048 or ECDSA, 15-year validity. Production: one certificate for `zaa.gl, *.zaa.gl`. Staging: a separate certificate for `staging.zaa.gl, *.staging.zaa.gl` on the staging VPS. | Trusted only by Cloudflare, which is fine because every host is proxied. Separate keys per VPS (rule 4). |
| Origin key handling | The private key is a **Docker secret** mounted read-only into `proxy` (repository rule). Never in the image, never in git. | Repository secret rule. |
| Authenticated Origin Pulls | **On.** Nginx requires the client certificate (`ssl_verify_client on`). Preferred: **per-hostname AOP with our own client certificate** (set through the API, OPS-013). Fallback: zone-level AOP with Cloudflare's shared certificate. | Zone-level AOP proves only that "the request came from *some* Cloudflare zone". With it, origin protection relies on the CF-only firewall plus Nginx `server_name` matching. Plan availability of per-hostname AOP: **[VERIFY]** |
| Minimum TLS version | 1.2 | |
| TLS 1.3 | On | |
| 0-RTT | **Off** | Replayable early data on POST/PATCH (tus, sign-in) is not acceptable. |
| Always Use HTTPS | On | |
| HSTS (edge) | `max-age=15552000`. `includeSubDomains` **only after** every host above, staging included, has passed an HTTPS check. **Preload off** until a separate decision. | A wrong `includeSubDomains` or preload cannot be undone quickly. |
| Universal SSL | On (covers `zaa.gl` and `*.zaa.gl`) | B2 for staging. |
| Certificate Transparency monitoring | On (free, emails on new certificates) | Detects certificates issued for our names. |

Origin firewall (nftables on the VPS, OPS-010/013): 443 is accepted only from Cloudflare's published IPv4/IPv6
ranges, refreshed by a timer. 80 is closed. SSH is accepted only from the admin allow-list. Nginx uses
`set_real_ip_from <CF ranges>; real_ip_header CF-Connecting-IP;` and passes the result as `X-Forwarded-For` to the API
(RUNBOOK "Client IP"). The API's `KnownProxies` lists only the `proxy` and `web` container addresses.

---

## 3. Caching

**Decision: Cloudflare caches nothing on `zaa.gl`, `api.zaa.gl`, `usercontent.zaa.gl` or any staging host, and that
includes public derived previews.** On `app.zaa.gl`, Cloudflare caches only static build assets (`/_next/static/*`,
which are content-hashed and immutable), following the origin's headers.

Why public previews are not cached either (the brief allowed it; I decided against it):
1. **Zero hit rate anyway.** Every `usercontent.zaa.gl` URL carries a short-lived ticket bound to one grant (addendum
   §8.1), so two viewers never request the same URL. A cache would store bytes without serving them again.
2. **Takedown, burn, expiry and privacy changes must take effect right away** (addendum §2.4, §8.2, §9, §13). A cached
   copy outlives them until it is purged. Purging is one more API call that can fail, and on Free it needs exact URLs.
3. The addendum §4.4 header rule is `Cache-Control: private, no-store` on **every** response. Caching previews would
   need a rule change from the product owner.
4. Less data passes through the CDN (B1).
Revisit only if the product owner wants stable, non-ticketed URLs for public previews (for example Open Graph images,
addendum §3.6). Those would need their own path, a purge-on-takedown job and a rule change. **OPEN (ARC-008).**

Implementation (belt and braces):
- Origin: `Cache-Control: private, no-store` (addendum §4.4). **Proposal to ARC-008/BE:** add `no-transform` on
  `usercontent.zaa.gl` (`private, no-store, no-transform`). Cloudflare honours `no-transform` and then does not
  compress or modify the body **[KNOWN]**. This keeps `Content-Length`, `Range`/`206` and stored-byte integrity exactly
  as the origin sent them. The header table belongs to the addendum, so this needs the owner's approval.
- **Cache Rule 1** "no-cache hosts": `http.host in {"zaa.gl" "api.zaa.gl" "usercontent.zaa.gl" "staging.zaa.gl" "api.staging.zaa.gl" "app.staging.zaa.gl" "usercontent.staging.zaa.gl"}` → **Bypass cache**.
- *Caching > Configuration*: Browser Cache TTL = **Respect existing headers**. Always Online = **Off** (it sets
  `cf_ob_info`/`cf_use_ob` cookies **[DOC 2026-11-09]** and could serve stale content). Crawler Hints = Off.
- `zaa.gl` redirects are never cached at the edge. The origin's `302` with `Cache-Control: private, max-age=0`
  (spec §7) already prevents it, and Cache Rule 1 makes it explicit. Every click has to reach the origin for counting,
  expiry and takedown.

---

## 4. Cookies set by Cloudflare (pre-launch condition, addendum §4.2.2 and §20.2)

Requirement: no response from `usercontent.zaa.gl` or `zaa.gl` carries `Set-Cookie`, and no host in the zone sets a
cookie that `usercontent.zaa.gl` would receive. A Cloudflare-set cookie with `Domain=.zaa.gl` breaks this as much as an
application cookie would.

Cloudflare's cookie reference **[DOC 2026-11-09]** lists `__cflb`, `__cf_bm`, `__cfseq`, `cf_clearance`,
`cf_ob_info`/`cf_use_ob`, `__cfwaitingroom`, `__cfruid`, `_cfuvid` and `cf_chl_rc_*`. That page **does not state the
`Domain` attribute** of any of them. **Plan for the worst case: assume each one may be set zone-wide** until the test
in §7 step 9 shows otherwise.

| Cookie | Set by | Decision |
|---|---|---|
| `__cf_bm` | Bot Fight Mode / Bot Management | **Bot Fight Mode OFF for the zone.** It "cannot be bypassed or skipped using WAF custom rules or Page Rules" and protects whole domains **[DOC 2026-11-09]**, so it cannot be limited to `app`. It also turns on JavaScript Detections, which cannot be disabled while it runs **[DOC]**. That injects `/cdn-cgi/challenge-platform/` script, which conflicts with the nonce CSP on the `/account/*` pages (spec §7). Super Bot Fight Mode (paid) is not used. |
| `cf_clearance`, `cf_chl_rc_*` | any challenge: Managed / JS / Interactive Challenge actions, Under Attack Mode, Security Level challenges, Turnstile pre-clearance, JavaScript Detections | **No challenge action anywhere in the zone.** WAF rules use **Block** only. Security Level at the lowest setting. Browser Integrity Check off. Under Attack Mode never on (an emergency exception needs the product owner, plus a cookie re-check afterwards). Turnstile on `app.zaa.gl` stays a widget whose token the API verifies (spec §2), with **pre-clearance OFF** in the Turnstile widget settings. Turnstile's own cookies live on `challenges.cloudflare.com`, not on `zaa.gl` **[KNOWN, VERIFY]**. |
| `_cfuvid` | Rate limiting rules that use that option | It is "only set when a site uses this option in a Rate Limiting Rule" **[DOC]**. Free counts by IP only **[DOC]**, so it should never appear. **[VERIFY]** in step 9. |
| `__cfruid` | previous rate-limiting version | Not used. **[VERIFY]** |
| `cf_ob_info`, `cf_use_ob` | Always Online | **Off** (§3). |
| `__cflb`, `__cfwaitingroom`, `__cfseq` | Load Balancer, Waiting Room, Sequence rules (paid) | Not used. |
| `CF_Authorization` | Cloudflare Access | **Not used for the staging gate.** Its cookie scope is outside this plan, and the gate does not need a cookie. Staging is gated by a WAF IP allow-list (rule C2) plus Nginx basic auth. |
| HTTP DDoS managed ruleset | always on | Some of its rules may use a challenge action during an attack, which would set `cf_clearance`. If the Free plan allows a ruleset override, set its action to **Block**. If not, record this as an accepted residual risk that only occurs during an active attack, and re-run step 9 after any attack. **[VERIFY]** |

Other zone features that change bodies or inject script must be **off**, at least for `usercontent.zaa.gl`, `api.zaa.gl`
and `zaa.gl` (Configuration Rule, §5). Safest is off for the whole zone. They would alter user bytes or the
API/account-page HTML:
Email Address Obfuscation, Rocket Loader, Automatic HTTPS Rewrites, Server-side Excludes, Hotlink Protection,
Speed Brain, Cloudflare Fonts, AI Labyrinth, Zaraz, Web Analytics automatic injection (RUM), Mirage/Polish (paid,
not used).

---

## 5. WAF, rate limits and rules within the Free plan

Free plan allowances, as I understand them: 5 WAF custom rules, 1 custom IP list, 10 each of cache, configuration,
redirect and transform rules **[KNOWN, VERIFY in the dashboard]**. Rate limiting on Free has these limits
**[DOC 2026-11-09]**: **1 rule**; expression fields **Path and Verified Bot only** (no host); counting by **IP**;
period **10 s**; mitigation timeout **10 s**.

The edge rules are a coarse first layer. The application limiters (RUNBOOK "Rate limits") and Nginx `limit_req`
(DEVOPS_PLAN §3) remain the real controls.

**WAF custom rules (5 of 5 used; every action is Block, never Challenge):**

| # | Name | Expression | Action |
|---|---|---|---|
| C1 | health never public | `starts_with(http.request.uri.path, "/health")` | Block |
| C2 | staging gate | `http.host in {"staging.zaa.gl" "api.staging.zaa.gl" "app.staging.zaa.gl" "usercontent.staging.zaa.gl"} and not ip.src in $zaagl_admin_ips` | Block |
| C3 | usercontent read-only | `http.host in {"usercontent.zaa.gl" "usercontent.staging.zaa.gl"} and not http.request.method in {"GET" "HEAD" "OPTIONS"}` | Block. `OPTIONS` agreed with ARC-008 §9: the viewers' cross-origin `fetch` with `Range` (react-pdf, chunked saves) sends a CORS preflight. Nginx answers `OPTIONS /d/<ticket>` itself with `204` (`Allow-Origin: https://app.zaa.gl`, `Allow-Methods: GET, HEAD`, `Allow-Headers: Range`, `Max-Age: 600`), so it never reaches the API, reveals nothing about the ticket and sets no cookie. ARC-008 §10's Nginx `if ($request_method !~ ^(GET\|HEAD\|OPTIONS)$) { return 405; }` matches this rule. |
| C4 | short host read-only | `http.host in {"zaa.gl" "staging.zaa.gl"} and not http.request.method in {"GET" "HEAD"}` | Block |
| C5 | reserved | Empty, kept for an emergency block (an ASN, a country or a path during an abuse wave) | Block |

`$zaagl_admin_ips` is the Free plan's single custom IP list: admin and CI egress addresses. **BLOCKED: the user
supplies the addresses.**

**Rate limiting rule (1 of 1):**

| Name | Match | Counting | Threshold | Action |
|---|---|---|---|---|
| R1 sign-in burst | `starts_with(http.request.uri.path, "/api/auth/")`. Path only, which is fine because only `api.zaa.gl` routes `/api/auth/*` (`IdentityEndpoints.cs:30`). | IP, 10 s | **50 requests / 10 s [EST]**. Deliberately looser than the app's `AuthPerMinute`, so the app answers first with a proper `429`. The edge only absorbs floods. Many users behind CGNAT share one IP: watch Security Events for the first 2 weeks. | Block, 10 s |

Who counts against R1:
- **Not the web app's server side.** The Next.js BFF reaches the API on the internal address
  (`ZAAGL_INTERNAL_API_URL`, backend network), not through Cloudflare, so its calls never hit the edge rule. Only
  browser requests that go to `https://api.zaa.gl/api/auth/*` directly (OAuth start and callback) count, per user IP.
- **CLI / desktop device flow.** `POST /api/auth/device/token` is polled every 5 s while a user approves the code
  (2 requests per 10 s per client). Users behind one office NAT or CGNAT IP share the budget: about 25 clients polling
  at once from one IP reach 50 per 10 s [EST]. Unlikely at launch, but it is the first thing to check if Security Events
  shows R1 blocks on that path. Fix then: raise R1, or exclude `/api/auth/device/token` from the expression and rely on
  the app limiter and the RFC 8628 `slow_down` response.

Redirects for C2's blocked hosts and other edge cases are not needed. One **redirect rule** is used:
`http.host eq "www.zaa.gl"` → 301 `https://app.zaa.gl/` (only if the product owner approves `www`).

**Configuration Rule 1** "inert hosts": `http.host in {"zaa.gl" "api.zaa.gl" "usercontent.zaa.gl" ...staging equivalents}`.
It sets: Email Obfuscation off, Rocket Loader off, Automatic HTTPS Rewrites off, Browser Integrity Check off,
Security Level lowest, Hotlink Protection off. (Redundant if those are already off zone-wide; kept in case someone
later turns one on at zone level.)

**Managed rules:** the Cloudflare Free Managed Ruleset stays on (it blocks, it does not challenge, **[VERIFY]**).
Check Security Events after the first staging upload test. A managed rule that matches binary tus `PATCH` bodies would
need a skip rule, which uses up C5.

**Body size:** Free plan request limit is 100 MB **[KNOWN]**. Uploads: tus `PATCH` at most 90,000,000 bytes
(addendum §6.1). Nginx on `api.zaa.gl` `/api/uploads`: `client_max_body_size 90000000;` with request buffering off.
Everything else on `api`: 2 MB. On `usercontent.zaa.gl` every response body is at most `Content:MaxResponseBytes`
(addendum §4.1.1). Cloudflare's published 100 MB limit applies to request bodies. The response cap is the product
owner's conservative choice under PD-38, and it does nothing about B1.

**Timeouts:** on Free, Cloudflare waits **100 s** for the origin's response headers (error `524` otherwise) **[KNOWN]**.
Upload `complete` already returns `202` and runs scanning asynchronously (addendum §6.5). Still to test on staging: one
90 MB `PATCH` from a slow client (10 Mbit/s, about 72 s of upload [EST]) completes without `524`, and so does a
90 MB `206` part at the same rate. **[VERIFY]**

**Network settings:** WebSockets **On** (SignalR statistics hub). HTTP/2 and HTTP/3 on. IPv6 Compatibility on.
Pseudo IPv4 off. gRPC off. Onion Routing off.

---

## 6. Monitoring tied to this plan
- **Cookie probe** (OPS monitoring, every 15 minutes): the script in §7 step 9, run against production. Any
  `set-cookie` from `zaa.gl` or `usercontent.zaa.gl`, any cookie with `domain=` from any host, or a cookie on
  `api.zaa.gl` outside `/api/auth/*` raises a page.
- **Zone reputation** (addendum §4.2.7, §20.3): Google Safe Browsing / Web Risk checks for `zaa.gl`,
  `usercontent.zaa.gl` and `app.zaa.gl`, plus Search Console ownership of the domain property. **BLOCKED: Google account
  (user).**
- CT log emails (§2) go to the security contact.
- Cloudflare notifications: "Security Events alert" and "Origin error rate (5xx)" are available on Free **[VERIFY]**.

---

## 7. Cloudflare dashboard checklist (in order)

Each step lists **what proves it is done**. Steps that need account access are **BLOCKED (B3)** until the user does
them or grants a scoped token.

1. **Zone.** Add `zaa.gl` to Cloudflare (Free plan). At the registrar, set the two Cloudflare nameservers. Remove any
   old A/AAAA/MX records that point at a server you control. *Proof:* `dig NS zaa.gl +short` shows Cloudflare's names.
   **BLOCKED (B3).**
2. **DNS records.** Create the records in §1, §1.1 and §1.2 exactly. Orange cloud on every A/AAAA. Grey only on
   mail TXT/CNAME. No wildcard. No grey record pointing at the VPS. *Proof:* `dig A usercontent.zaa.gl +short` returns
   Cloudflare IPs (`104.x`/`172.6x.x`), never `<ORIGIN_V4>`. **BLOCKED (B3, B4, B5).**
3. **DNSSEC.** *DNS > Settings > Enable DNSSEC*, then add the DS record at the registrar. **BLOCKED (B3).**
4. **SSL/TLS.**
   - *Overview*: mode **Full (strict)**.
   - *Edge Certificates*: Always Use HTTPS **on**, Min TLS **1.2**, TLS 1.3 **on**, 0-RTT **off**, Automatic HTTPS
     Rewrites **off**, CT monitoring **on**. HSTS stays off until step 10.
   - *Origin Server*: create the Origin CA certificate for `zaa.gl, *.zaa.gl` and hand the key to the deploy secret
     store (Docker secret), never by email or chat. Turn on **Authenticated Origin Pulls**.
   - Staging: B2 first (ACM, or a rename).
   **BLOCKED (B2, B3).**
5. **Bots and Security settings.**
   - *Security > Bots*: **Bot Fight Mode OFF**. Leave "Block AI bots" off unless it is later shown to set no cookie
     and to block only.
   - *Security > Settings*: Security Level **lowest** ("Essentially Off" or the equivalent), Browser Integrity Check
     **off**, Challenge Passage irrelevant (no challenges).
   - Never use "I'm Under Attack".
   - *Turnstile > widget > Settings*: **Pre-clearance OFF**.
   - *Security > DDoS*: if an override is offered on Free, set the HTTP DDoS ruleset action to Block.
   **BLOCKED (B3).**
6. **Scrape Shield, Speed, Caching and Zaraz.**
   - *Scrape Shield*: Email Address Obfuscation **off**, Hotlink Protection **off**.
   - *Speed > Optimization*: Rocket Loader **off**, Speed Brain **off**, Cloudflare Fonts **off**.
   - *Caching > Configuration*: Browser Cache TTL **Respect existing headers**, Always Online **off**, Crawler Hints
     **off**.
   - *Zaraz*: not enabled. *Web Analytics*: no automatic injection.
   **BLOCKED (B3).**
7. **Rules.**
   - *Caching > Cache Rules*: Cache Rule 1 (§3).
   - *Rules > Configuration Rules*: Configuration Rule 1 (§5).
   - *Rules > Redirect Rules*: the `www` rule, if approved.
   - *Security > WAF > Tools/Lists*: create the IP list `zaagl_admin_ips`.
   - *Security > WAF > Custom rules*: C1 to C4, all **Block**.
   - *Security > WAF > Rate limiting rules*: R1.
   - *Network*: WebSockets on, IPv6 on, Onion Routing off, Pseudo IPv4 off.
   **BLOCKED (B3).**
8. **Origin side (OPS-010/013, after a VPS exists).**
   - nftables: 443 accepted only from Cloudflare ranges.
   - Nginx: `ssl_verify_client on` with the AOP certificate, real IP from `CF-Connecting-IP`, default server `444`.
   *Proof:* `curl -vk --resolve usercontent.zaa.gl:443:<ORIGIN_V4> https://usercontent.zaa.gl/` from a
   non-Cloudflare machine times out or is refused.
   **BLOCKED (B5, VPS).** **Not verifiable here: no Docker, no VPS.**
9. **Cookie check (the pre-launch condition).** Run this against staging first, then production. Before running it,
   make sure Security Events shows no challenge actions.
   ```bash
   for u in https://zaa.gl/ https://zaa.gl/doesnotexist https://zaa.gl/.well-known/security.txt \
            https://usercontent.zaa.gl/ https://usercontent.zaa.gl/t/invalid \
            https://api.zaa.gl/api/me https://api.zaa.gl/openapi/v1.json \
            https://app.zaa.gl/ https://www.zaa.gl/ ; do
     echo "== $u"; curl -s -o /dev/null -D - "$u" | grep -i '^set-cookie' || echo "(no cookie)"
   done
   ```
   Expected: `(no cookie)` for every URL. On `app.zaa.gl`, only `__Host-` cookies without `Domain` are allowed. Then do
   the same in a browser: sign in with Google and GitHub, open a viewer, play a video and download a file. Afterwards,
   DevTools > Application > Cookies must show **no cookie with domain `.zaa.gl`**, no cookie at all for
   `usercontent.zaa.gl` and `zaa.gl`, and only the `.zaagl.external` / correlation cookies on `api.zaa.gl`, gone after
   the callback (PD-46). Record the output with the date in the release evidence. **BLOCKED until the hosts exist.**
10. **HSTS.** Turn it on only after step 9 passes and every host, staging included, answers over HTTPS:
    `max-age=15552000`, `includeSubDomains` on, preload **off**, nosniff on. **BLOCKED (B3).**
11. **CAA check.** `dig CAA zaa.gl +short` includes every CA that *Edge Certificates* lists (§1.2).
12. **Terms (B1).** The product owner records the B1 decision in DECISIONS.md before Stage 7.

---

## 8. What is evidence and what is an estimate

| Statement | Basis |
|---|---|
| CDN terms require Paid Services for video and other large files on non-Enterprise plans | **[DOC 2026-11-09]** cloudflare.com Service-Specific Terms (Application Services), page dated 2026-09-28 |
| Bot Fight Mode cannot be skipped by WAF/Page Rules and enables JavaScript Detections | **[DOC 2026-11-09]** developers.cloudflare.com/bots/get-started/bot-fight-mode |
| Cloudflare cookie names and their products. The `Domain` attribute is not documented. | **[DOC 2026-11-09]** developers.cloudflare.com/fundamentals/reference/policies-compliances/cloudflare-cookies |
| Free rate limiting: 1 rule, Path/Verified Bot fields, IP counting, 10 s period and timeout | **[DOC 2026-11-09]** developers.cloudflare.com/waf/rate-limiting-rules |
| 100 MB request limit, 100 s origin timeout, Universal SSL covers one subdomain level, Cloudflare honours `no-transform`, CAA auto-add, Free rule counts | **[KNOWN]**, not re-read today, checked during the dashboard steps |
| R1 threshold, ACM price, upload/part times | **[EST]** |
| Origin firewall, AOP, Nginx behaviour, the cookie test | **Not run.** No Docker on this machine (firmware virtualisation off), no VPS, no account access. |
