# ARC-008 · User-content host and delivery path (`usercontent.zaa.gl`)

| | |
|---|---|
| Owner | zaagl-architect |
| Status | DESIGN for BE-018/BE-019 (authorize hop, grants), OPS-001/OPS-010 (DNS, Cloudflare, Nginx). Not implemented. |
| Decisions | PD-01, PD-02, PD-03 (changed: `zaa.gl` subdomain), PD-05, PD-12, PD-37, PD-38, PD-45 (ADR-0001, 0002, 0003, 0005, 0008, 0014, 0015, 0021) |
| Normative | addendum §4 (hosts, headers), §8 (tickets, burn), §16 MUST NOT 1–6, 25 |
| Contracts | C2 `IDownloadGrants`, `DeliveryPurpose`, `DeliveryOutcome` (`src/Modules/Resources/Contracts/ResourceContracts.cs:157-225`); C3 ticket format; C7 `IObjectStorage.GetInternalPath` (`src/Modules/Files/Contracts/FileContracts.cs`, `StorageArea` and `GetInternalPath`) |

Values marked **(to confirm)** are product values the owner has not approved; they are configuration and tests read
them from configuration. Topics marked **OPEN** must not be implemented until decided.

## 1. Host table

| Host | Cloudflare | Serves | Cookies | Credentials accepted |
|---|---|---|---|---|
| `zaa.gl` | proxied | `302` redirects, static `404/410` pages (addendum §17), `302` to viewers for content links | never | none |
| `app.zaa.gl` | proxied | Next.js app, BFF `/bff/*`, viewers `/p`, `/i`, `/f`, `/activate` | host-only `__Host-*` only | session cookie (BFF) |
| `api.zaa.gl` | proxied | JSON API, tus uploads (≤ 90,000,000-byte requests, PD-12), ticket mint | none, except the OAuth handshake routes (PD-46) | bearer JWT, API key |
| `usercontent.zaa.gl` | **proxied (PD-38)** | every user byte, only via `/d/<ticket>` | **never** (addendum §4.2.1–2) | **none**: ticket only; `Authorization` and `Cookie` are ignored (§4.2.6) |

`Content:UserContentBaseUrl = https://usercontent.zaa.gl`. Ticket URLs are always built from this value, never from a
request `Host` header.

## 2. Request flow (mint to bytes)

```
viewer (app.zaa.gl) --BFF--> POST api.zaa.gl/api/public/content/{code}/download   (+ X-Zaagl-Link-Key, X-Zaagl-Grant)
  api: IAccessGate.EvaluateAsync(code, creds, Download)   -> Allowed or 404/401/410
  api: IDownloadGrants.MintAsync(resourceId, Download)     -> grant row + ticket, exp = now + Content:DownloadTicketSeconds
  <- 200 { url: "https://usercontent.zaa.gl/d/<ticket>", expiresAt }
browser GET usercontent.zaa.gl/d/<ticket>  (Cloudflare -> Nginx)
  nginx location /d/  -> proxy to http://api:8081/internal/delivery/<ticket>   (authorize hop, internal port)
  api: IDownloadGrants.AuthorizeAsync(ticket, isHead)       -> DeliveryDecision
       + size/Range check (§5) + IStoredObjects.GetForResourceAsync -> storage key, detected MIME, size
  <- 204 + X-Accel-Redirect: /_protected/objects/3f/a2/<uuid> + Content-Type + Content-Disposition + X-Zaagl-* headers
  nginx internal location /_protected/  -> sendfile with Range, adds the §4.4 headers
<- 200/206 bytes
```

Inline, preview and raw tickets (`DeliveryPurpose.Inline/Preview/Raw`) are minted by the descriptor endpoint
(`GET /api/public/content/{code}`) after the gate with purpose `View`/`Preview`/`Raw`; lifetime
`Content:InlineTicketMinutes` (15). They are never minted for burn files (addendum §8.1.3). Owner tickets
(`POST /api/resources/{id}/download`, purpose `Owner`) skip the gate (owner check instead) and never burn.

## 3. Ticket format (C3, fixed)

`<b64url(payload)>.<b64url(HMAC-SHA256(key[kid], "zaagl/ticket/v1\n" || payload))>`

Payload, binary, big-endian:

| Field | Size |
|---|---|
| version | 1 byte, `0x01` |
| kid | 1 length byte (1..16) + ASCII |
| grant id | 16 bytes |
| resource id | 16 bytes |
| purpose | 1 byte (`DeliveryPurpose` value) |
| derivative | 1 length byte (0..32) + ASCII (0 = none) |
| exp | int64, Unix seconds |

Rules: reject any ticket string longer than 256 characters before decoding (the same 256 limit applies to access
grants, ARC-004 §4.3, and is the `maxLength` in openapi-phase1.yaml); unknown version or kid → `ticket-invalid`;
compare the MAC in constant time (`CryptographicOperations.FixedTimeEquals`); keys `Content:TicketKeys` (rotating set,
newest signs, all verify). Access grants (ARC-004) use the same shape with domain `"zaagl/grant/v1\n"` and separate keys,
so a grant can never verify as a ticket. A ticket appears in Cloudflare and Nginx logs as a path; Nginx logs MUST
redact it (log `$uri` with the ticket replaced, OPS-022). Lifetimes: `Content:DownloadTicketSeconds` (120) while unused,
`Content:InlineTicketMinutes` (15).

## 4. Authorize hop

### 4.1 Endpoint and isolation (decided here, technical)
- Route: `GET|HEAD /internal/delivery/{ticket}` on a **second Kestrel endpoint `http://0.0.0.0:8081`** inside the API
  container, mapped with `RequireHost("*:8081")`. Port 8081 is not published by Docker and only the `proxy` (Nginx)
  container reaches it on the internal network. No public server block proxies to `/internal/`.
- Rejected: a shared-secret header added by Nginx. A secret lives in config and logs and can leak; a port that is not
  routable from outside cannot be reached at all. The public `api.zaa.gl` server block additionally returns `404` for
  any `/internal/` path (defence in depth).
- The hop is anonymous (no auth scheme), sets no cookie, and ignores `Authorization`/`Cookie`.

### 4.2 Inputs from Nginx
`Range` and `If-Range` (passed by default), `X-Zaagl-Method: $request_method` (so HEAD is explicit even if a proxy
rewrites it), `X-Forwarded-For: $remote_addr` (after `real_ip` from Cloudflare, §8), `User-Agent`. The API trusts
`X-Forwarded-For` only from the proxy's exact address (`Network:KnownProxies`, BE-030,
`src/Zaagl.Api/Infrastructure/ForwardedHeadersSetup.cs:26-60`).

### 4.3 Decision order
1. Length ≤ 256, decode, version, kid, MAC, purpose value valid → else `403 ticket-invalid`.
2. `IDownloadGrants.AuthorizeAsync(ticket, isHead)`: grant exists and matches resource and purpose; resource state
   (addendum §2.4) `ready`, or burned within its own grace for the claiming grant → mapping in §4.5.
3. Size and Range check (§5). Done **before** any burn claim or request count, so a refused request consumes nothing.
4. For a burn file, purpose `Download`, method GET: claim or count (§6). HEAD never claims and never counts.
5. No analytics event is written here until PD-10 is approved (C6). The owner's download count comes only from the
   grant table (`download_grants`), the single source of truth.
6. Answer `204` with the headers of §4.4.

### 4.4 Allowed response (hop → Nginx)
| Header | Value |
|---|---|
| `X-Accel-Redirect` | `IObjectStorage.GetInternalPath(storageKey)`, e.g. `/_protected/objects/3f/a2/<uuid>` (areas `objects`, `previews`, `thumbnails`; never `temp` or `quarantine`) |
| `Content-Type` | detected MIME (addendum §7.2); `text/plain; charset=utf-8` for raw; `application/octet-stream` for attachments of unknown type |
| `Content-Disposition` | §7 |
| `X-Zaagl-Acao` | `https://app.zaa.gl` for purposes `Inline` and `Preview` only; absent otherwise (§9) |
| `X-Accel-Buffering` | `no` |

Nginx keeps `Content-Type` and `Content-Disposition` from the upstream response on an X-Accel redirect; every other
header is added by the internal location (§10). The hop sends no `Cache-Control` and no `Set-Cookie`.

### 4.5 Refusals (problem JSON, `type: https://zaa.gl/problems/<code>`, `code` extension)
| `DeliveryOutcome` / check | Status | `code` |
|---|---|---|
| bad, expired, forged, other-resource, other-purpose ticket; grant exhausted | `403` | `ticket-invalid` |
| `Burned` (another grant won, or grace over) | `410` | `burned` |
| `Gone` (expired, deleted, disabled, taken down, purgeable) | `410` | `gone` (this host only; the API routes give the specific code, ARC-004 §3) |
| `Processing` | `409` | `processing` |
| `Rejected` | `422` | `content-rejected` |
| object above `Content:MaxResponseBytes` without an acceptable Range (§5) | `416` | `range-required` |

Error bodies pass through Nginx's `/d/` location, which adds the full §10 header set (`always`), so errors are inert too
(addendum §4.2.5). Problem bodies never echo the ticket.

## 5. Per-response cap `Content:MaxResponseBytes` (PD-38)

Value **(to confirm)**, proposed 90,000,000 bytes. Rule (addendum §4.1.1.1, MUST NOT 25): no response body from this
host exceeds it. Mechanism (technical, decided here):
- Nginx `max_ranges 1;` in the internal location: multi-range requests are answered as a single full response, so they
  must be refused earlier; the hop refuses any `Range` with more than one range on a capped object.
- Object size ≤ cap: any request allowed (full `200` or any single range `206`).
- Object size > cap: the request MUST carry exactly one byte range whose served length is ≤ cap: `bytes=a-b` with
  `b - a + 1 ≤ cap`, or suffix `bytes=-n` with `n ≤ cap`. Open-ended `bytes=a-` is allowed only if `size - a ≤ cap`.
  Anything else → `416 range-required` with `Accept-Ranges: bytes` and `Content-Range: bytes */<size>`, so a client
  learns the size and can fetch parts. `If-Range` is passed through; Nginx evaluates it against the file's ETag.
- HEAD of a capped object is allowed (no body) and reports `Content-Length: <size>` and `Accept-Ranges: bytes`.
- Previews, thumbnails and snippets are always below the cap (snippet caps, PD-56, are at most 2 MB).

In Phase 1 only Starter files between the cap and 100 MB need two parts.

**OPEN (addendum §4.1.1.4, ARC-008 + FE):** how the browser saves such a file as one download. Options:
(a) the viewer downloads `Range` parts with `fetch` and assembles them with the File System Access API or a Blob
(memory up to 100 MB; Safari lacks the File System Access API); (b) a service worker on `app.zaa.gl` streams the parts
into one download (works everywhere modern, more code); (c) the server offers numbered parts (`.part1`, `.part2`) the
user joins (poor UX). Recommendation: (a) with Blob fallback, because the Phase 1 maximum is 100 MB. Until decided,
whole-file downloads above the cap are NOT_TESTED and the CLI/desktop fetch ranges themselves.

## 6. Burn interplay (PD-05, addendum §8.2)

- Only a `Download` ticket minted by the button POST can claim. Bots never POST, so the rule is User-Agent free.
- First GET: the claim of ARC-006 §5 (single statement of the rule and the SQL; columns `burn_claimed_at`,
  `burn_grant_id`, `download_grants.request_count`). Exactly one winner; every other grant's first GET → `410 burned`.
- After the claim the winning grant is governed by its row, not by the ticket's 120 s `exp`: further GETs are allowed
  while `now < burn_claimed_at + Burn:GraceMinutes` (30) and `request_count < Burn:MaxRequests` (16), each counted by a
  conditional `UPDATE ... SET request_count = request_count + 1 WHERE request_count < max` (ARC-006 §5 step 3). The ticket MAC is still
  verified on every request; only the `exp` check is replaced by the grace window for the claiming grant.
- HEAD: never claims, never counts; allowed before the claim (state `ready`) and for the claiming grant within grace.
- Requests refused by §5 consume nothing (check runs before the claim).
- Bytes are deleted after the grace window by the burn finaliser job (ARC-012); no previews or inline tickets exist for
  burn files, so nothing else references the bytes.
- 16 requests ≥ 2 parts plus retries for the largest Phase 1 file (addendum §4.1.1.5).

## 7. Content-Type and Content-Disposition

- `Content-Type` is the stored detected type, never the declared one (MUST NOT 4).
- `inline` only when purpose is `Inline` or `Preview` **and** the detected type is on `Content:InlineMimeAllowlist`.
  Proposed initial list: `image/png`, `image/jpeg`, `image/gif`, `image/webp`, `video/mp4`, `video/webm`. The final
  media allowlist is **OPEN (PD-16)**; SVG is never inline (addendum §4.4; PD-18 proposed). Derived previews are
  produced as PNG/JPEG/WebP/MP4 and are therefore inline.
- Every other case is `attachment`: purposes `Download`, `Owner`, `Raw` (addendum §4.4 lists raw as attachment), SVG,
  HTML, XML, PDF and unknown types.
- Format (RFC 6266 / RFC 8187): `attachment; filename="<ascii>"; filename*=UTF-8''<pct-encoded>`. `<ascii>` is the
  sanitised display name (addendum §7.2) with every non-ASCII, control, `"`, `\`, `/`, `;` character replaced by `_`,
  at most 150 bytes, keeping the extension. Snippets use `<code>.txt` (technical default, not a product rule). Inline responses send
  `inline; filename=...` in the same format.

## 8. Cloudflare and forwarded headers (OPS-001)

- DNS: `usercontent` CNAME/A **proxied**. TLS: Full (strict) with an Origin CA certificate; Authenticated Origin Pulls
  on, and the host firewall accepts 443 only from Cloudflare ranges.
- Cache Rule: hostname `usercontent.zaa.gl` → **Bypass cache** (plus origin `Cache-Control: private, no-store`).
- No zone feature may set a cookie on this host or on `.zaa.gl` (addendum §4.2.2): Bot Fight Mode / Super Bot Fight
  Mode (`__cf_bm`), interactive challenges (`cf_clearance`) and Turnstile pre-clearance must be off or scoped away from
  this host; verify with a cookie check before launch (addendum §20.2).
- Off for this host (Configuration Rule): Rocket Loader, Email Obfuscation, Automatic HTTPS Rewrites, Mirage/Polish,
  any HTML-modifying feature; they must not touch user bytes.
- WAF managed rules on, but with action Block/Log, never Challenge, on this host (a challenge page would set a cookie).
- Real client IP: Nginx `set_real_ip_from <Cloudflare ranges>; real_ip_header CF-Connecting-IP;` (ranges file refreshed
  by OPS); `CF-Connecting-IP` from any other source is ignored. Nginx then sends `X-Forwarded-For $remote_addr` to the
  hop, and the API trusts it only from the proxy (BE-030).
- Upload size limits do not apply here (no uploads on this host; Nginx `client_max_body_size 1k`, only GET/HEAD/OPTIONS).

### 8.1 Cloudflare terms (PD-38 risk, PD-52 DEFERRED)

PD-38 asked ARC-008 to record the risk of Cloudflare's self-serve terms. `design/ops-001-hosts-and-dns.md` B1 found
that the CDN terms require Paid Services for video and other large non-web files on non-Enterprise plans; the user
deferred the choice as PD-52 (before Stage 7 launch). Effect on this design: options (a) DNS-only on a second IP and
(c) a separate domain keep §2-§7 but drop the per-response cap of §5 (and change the host table, C3); option (b) paid
R2/Stream moves byte delivery off this Nginx path entirely (new C3 and C7 implementation); option (d) accepting the
risk keeps this design as written. Nothing here may assume an outcome until PD-52 is decided.

## 9. CORS (react-pdf, `<video>` range fetches)

- Only purposes `Inline` and `Preview` get `Access-Control-Allow-Origin: https://app.zaa.gl` (via `X-Zaagl-Acao`);
  never `*`, never `Access-Control-Allow-Credentials`. `Access-Control-Expose-Headers: Content-Length, Content-Range,
  Accept-Ranges`.
- The viewers' `fetch` to this host needs `connect-src` to allow it on `app.zaa.gl` (ADR-0023, class N). Cloudflare WAF
  rule C3 (ops-001) allows GET, HEAD and OPTIONS on this host, matching the Nginx method filter below.
- Preflight `OPTIONS /d/<ticket>` is answered by Nginx itself with `204`, `Access-Control-Allow-Origin:
  https://app.zaa.gl`, `Allow-Methods: GET, HEAD`, `Allow-Headers: Range`, `Max-Age: 600`. It reveals nothing about
  the ticket and does not reach the API.
- PDF preview: PDFs are attachment-only types, so react-pdf needs a `Preview`-purpose ticket on the original PDF with
  `Content-Disposition: attachment` and CORS. `fetch` ignores `Content-Disposition`, so this works. PDF preview method is
  PD-31 (PROPOSED).
- The allowed origin `https://app.zaa.gl` is configuration (`Content:AppOrigin`); staging uses its own app host.

## 10. Nginx reference configuration (OPS-010)

```nginx
# /etc/nginx/snippets/ucd-headers.conf  -- included in EVERY location of this server (add_header is not inherited
# once a location defines its own).
add_header X-Content-Type-Options "nosniff" always;
add_header Content-Security-Policy "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'" always;
add_header Cache-Control "private, no-store" always;
add_header Referrer-Policy "no-referrer" always;
add_header Cross-Origin-Resource-Policy "cross-origin" always;
add_header X-Robots-Tag "noindex, nofollow" always;

# conf.d/usercontent.conf
upstream api_internal { server api:8081; keepalive 16; }

map $request_uri $ucd_log_uri { ~^/d/ "/d/<redacted>"; default $request_uri; }
log_format ucd_json escape=json '{"t":"$time_iso8601","ip":"$remote_addr","m":"$request_method","u":"$ucd_log_uri",'
                                '"s":$status,"b":$body_bytes_sent,"range":"$http_range","done":"$request_completion"}';

server {
    listen 443 ssl;
    http2 on;
    server_name usercontent.zaa.gl;
    ssl_certificate     /etc/nginx/certs/origin-zaa.gl.pem;
    ssl_certificate_key /etc/nginx/certs/origin-zaa.gl.key;
    ssl_client_certificate /etc/nginx/certs/cloudflare-aop.pem;   # Authenticated Origin Pulls
    ssl_verify_client on;

    include /etc/nginx/snippets/cloudflare-realip.conf;          # set_real_ip_from ...; real_ip_header CF-Connecting-IP;
    access_log /var/log/nginx/ucd.json ucd_json;
    client_max_body_size 1k;
    server_tokens off;

    if ($request_method !~ ^(GET|HEAD|OPTIONS)$) { return 405; }

    location ~ "^/d/[A-Za-z0-9_-]{1,200}\.[A-Za-z0-9_-]{43}$" {
        include /etc/nginx/snippets/ucd-headers.conf;
        if ($request_method = OPTIONS) {
            add_header Access-Control-Allow-Origin "https://app.zaa.gl" always;
            add_header Access-Control-Allow-Methods "GET, HEAD" always;
            add_header Access-Control-Allow-Headers "Range" always;
            add_header Access-Control-Max-Age "600" always;
            include /etc/nginx/snippets/ucd-headers.conf;
            return 204;
        }
        rewrite ^/d/(.*)$ /internal/delivery/$1 break;
        proxy_pass http://api_internal;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host usercontent.zaa.gl;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Zaagl-Method $request_method;
        proxy_set_header Authorization "";
        proxy_set_header Cookie "";
        proxy_pass_request_body off;
        proxy_set_header Content-Length "";
        proxy_hide_header Set-Cookie;
        proxy_hide_header Cache-Control;
        proxy_hide_header Expires;
        proxy_intercept_errors off;
        proxy_read_timeout 10s;
    }

    location /_protected/ {
        internal;
        alias /srv/zaagl/storage/;           # read-only mount: objects, previews, thumbnails (DEVOPS_PLAN §2.2)
        sendfile on;
        tcp_nopush on;
        max_ranges 1;
        etag on;
        default_type application/octet-stream;
        include /etc/nginx/snippets/ucd-headers.conf;
        add_header Access-Control-Allow-Origin $upstream_http_x_zaagl_acao always;   # empty -> not sent
        add_header Access-Control-Expose-Headers "Content-Length, Content-Range, Accept-Ranges" always;
    }

    location / {
        include /etc/nginx/snippets/ucd-headers.conf;
        return 404;
    }
}
```

Notes: this block is a reference; OPS validates it with `nginx -t` in CI (OPS-010). The regex enforces the 256-character ceiling and the
two-part shape (43-char MAC) before any API call. `/_protected/` must map only `objects/`, `previews/`, `thumbnails/`;
`temp/` and `quarantine/` are not mounted in the proxy container (DEVOPS_PLAN.md §2.2 line 88). The hop's
`X-Accel-Redirect` path is generated from `GetInternalPath` and validated against `^/_protected/(objects|previews|thumbnails)/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}$`
before it is sent.

## 11. Open questions for the user

1. **Saving files larger than one part (90–100 MB, Starter only).** Options: (a) the viewer fetches parts and joins
   them in the browser; (b) a service worker streams them into one download; (c) offer the file as numbered parts.
   Recommendation: (a), simplest for 100 MB maximum.
2. **`Content:MaxResponseBytes` value.** Proposed 90,000,000 bytes (same as upload chunks). Recommendation: approve.
3. **Raw snippet text in the browser.** The approved rulebook makes "raw" an attachment (it downloads). Option: allow
   raw text to open inline as `text/plain` under the sandbox CSP (safe, but an addendum change). Recommendation: allow
   inline raw view once PD-09 is decided; keep attachment until then.
4. **Inline media list.** Which formats open in the browser instead of downloading is part of PD-16 (OPEN).

## 12. Review checklist for BE / OPS

- [ ] `/internal/delivery/*` only on port 8081; `api.zaa.gl/internal/*` → 404; port 8081 not published.
- [ ] Ticket parser: length ≤ 256 before decode, version/kid/MAC/purpose checks, constant-time compare, key rotation.
- [ ] Every response from the host (200, 206, 204 OPTIONS, 4xx, 5xx, Nginx default errors) carries the six §10 headers
      and no `Set-Cookie` (test with a `Set-Cookie` injected by a fake upstream).
- [ ] Non-allowlisted types and every `Download`/`Owner`/`Raw` response are `attachment` with RFC 6266 encoding.
- [ ] Capped object: no Range → `416 range-required`; multi-range → 416; range > cap → 416; range ≤ cap → 206.
- [ ] Refused requests (§5) never claim burn or count a request; HEAD never claims or counts.
- [ ] Concurrent first GETs on a burn ticket: exactly one 206/200, the rest `410 burned`.
- [ ] Claiming grant works past the ticket's 120 s `exp` within 30 min and 16 requests, not after.
- [ ] `X-Accel-Redirect` path validated; `temp/` and `quarantine/` unreachable.
- [ ] Ticket redacted in Nginx and app logs; Cloudflare cache bypass rule present; no `__cf_bm`/`cf_clearance` cookie.
- [ ] Real IP only from Cloudflare ranges; API trusts `X-Forwarded-For` only from the proxy (BE-030).
- [ ] CORS header only for `Inline`/`Preview`, origin exactly `https://app.zaa.gl`, never credentials.
