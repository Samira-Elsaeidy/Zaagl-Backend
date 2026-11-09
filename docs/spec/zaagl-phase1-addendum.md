# Phase 1 addendum to spec v9.4: snippets, media and file sharing

**Version 1.4, APPROVED 2026-11-09 as the official Phase 1 rulebook (PD-34).** Owner: zaagl-architect (ARC-001).
Changes go through the product owner. This addendum is the normative oracle QA tests Phase 1 against. It extends
`docs/spec/zaagl-spec-v9.4.md` ("the spec"); every rule of the spec still applies unless a section below changes it
explicitly. It reflects the decisions approved on 2026-11-09 in `docs/phase1-implementation/DECISIONS.md` (Approvals
table): PD-01 to PD-07, PD-12, PD-14 (as updated), PD-15, PD-19, PD-23 to PD-25, PD-28, PD-34, PD-36 to PD-47 and
PD-53 to PD-57 (PD-52 deferred). Contracts are in
`docs/phase1-implementation/contracts.md`; the reasons for each decision are in `docs/phase1-implementation/adr/`.
Version history: Appendix B.

A rule marked **MUST** is not negotiable. A value marked **(to confirm)** is a proposal the product owner has not
approved; tests read it from configuration and must not hard-code it. A topic marked **OPEN (PD-nn)** is undecided:
implementations must not invent behaviour for it, and QA records the related tests as NOT_TESTED until it is decided.
A topic marked **PROPOSED (PD-nn)** has a recommendation in DECISIONS.md that is **not approved**; for implementation and
QA it is treated exactly like OPEN.

---

## 1. Scope and terms

- Phase 1 adds three **content kinds** to the Phase 0 link engine: **snippet** (text and code, stored in PostgreSQL),
  **media** (images, animations, SVG, screen recordings) and **file** (any other upload).
- A **resource** is one piece of content. Every resource is reached through exactly one **content link**: a normal
  `links` row whose target is the resource instead of a URL (§2).
- **Account content** belongs to a signed-in user. **Guest content** is created without credentials (§11).
- **User-content host**: `usercontent.zaa.gl` (§4). **Viewer**: the trusted UI pages on `app.zaa.gl` (§4).
- Plans in Phase 1 are **Free** and **Starter** only (PD-04). Spec v9.4 says "Premium"; for Phase 1, "premium" in the spec
  means "a plan whose capability flag allows it" (§10), which is Starter today.
- Route prefix: Phase 0 code serves `/api/...`, the spec says `/api/v1/...`. The prefix and versioning scheme are
  **OPEN (PD-33, ARC-016)**. Routes below are written `/api/...`.

## 2. Content link model

### 2.1 Link target
1. `links.links` MUST gain `target_kind smallint not null default 0` (`0 url`, `1 snippet`, `2 file`, `3 media`) and
   `resource_id uuid null`, with the check `(target_kind = 0) = (resource_id IS NULL)`. The migration is expand-only
   (spec MUST NOT #32); any new index on `links.links` is created `CONCURRENTLY`.
2. `destination_url` and `target_host` become nullable for content kinds only. Every URL-only code path (normalisation,
   destination blocklist, Web Risk lookup and re-check, routing rules, fallback URL, UTM, library search on the
   destination, purge archive, export of `TargetUrl`) MUST branch on `target_kind` and MUST NOT run for content kinds.
3. URL links (`target_kind = 0`) MUST behave exactly as in Phase 0. All Phase 0 tests pass unchanged.
4. A content link is created only through `ILinkIssuer.CreateContentLinkAsync` (contract C1). It reuses the Phase 0
   creation path unchanged: leased-range codes, reserved-word skipping, alias syntax and generator-reachability
   (spec §4.5), denylist, `blocked_aliases`, restore freeze, advisory alias lock, idempotency. The resource id is the
   idempotency key.
5. The link-cache entry gains `TargetKind` and `ResourceId`; the cache key MUST be versioned so entries written by an
   older build are never deserialised without them.

### 2.2 Order of operations for every share
1. Resources inserts the resource as `pending` and reserves quota (§10) in one transaction.
2. `ILinkIssuer.CreateContentLinkAsync` with the resource id as idempotency key.
3. Resources stores the link id on the resource.
4. A sweeper deletes `pending` resources older than 24 hours that have no link, and releases their reservation.

Cross-module writes are not atomic; every step is idempotent and re-runnable.

### 2.3 Link and resource coupling
- The link's `expires_at` and `purge_after` are authoritative for the resource. Link lifecycle changes reach Resources
  through the Links-owned outbox `links.lifecycle_outbox` (events `LinkDeleted`, `LinkDisabled`, `LinkEnabled`,
  `LinkRenewed`, `LinkExpired`, `LinkPurged`; contract C10), written in the same transaction as the change and drained
  by the worker at least once. Handlers MUST be idempotent.
- A resource-side block (malware verdict, blocked hash, admin takedown of content) MUST disable the link through
  `ILinkTakedown` with `disabled_kind = safety` (malware) or `abuse` (everything else) (§13).

### 2.4 Resource state
Computed by one total function, evaluated top to bottom, first match wins. Never stored.

| # | State | Condition | Viewer / descriptor | Bytes served |
|---|---|---|---|---|
| 1 | `purgeable` | link `now ≥ purge_after` | `404` | no |
| 2 | `deleted` | link or resource `deleted_at` set | `410` | no |
| 3 | `disabled` | link `disabled_at` set | `410` (abuse/safety: acceptable-use page; §17) | no |
| 4 | `burned` | `burn_claimed_at` set (§8) | `410` "downloaded and deleted" | only the claiming grant, within its grace |
| 5 | `expired` | link `now ≥ expires_at` | `410` (quarantined) | no |
| 6 | `processing` | `processing_status` in pending, scanning, error | `200` descriptor with `state: processing` | **no** |
| 7 | `rejected` | `processing_status = rejected` | `422 content-rejected` | **no** |
| 8 | `ready` | otherwise | `200` | yes, by ticket (§8) |

Invariant (tested): **no bytes of a resource are ever served unless its state is `ready`**, except the burn grace of
state 4.

## 3. Resolution flow

1. `GET/HEAD zaa.gl/<code>` keeps the spec §7.3 rules (path checks before I/O, cache, rate limits, headers). BE-031
   MUST make 1–3 character paths and multi-segment paths return `404` before any I/O and add the §7.3 redirect and
   tiered miss limits.
2. For a content link the redirect computes the link state (spec §4.3). `purgeable` → `404`; `deleted`, `disabled`,
   `quarantined` → the small static `410` pages served by `zaa.gl` itself (§17, PD-39; content links have no fallback
   URL, §2.1.2); `active` → **`302 https://app.zaa.gl/{p|i|f}/<code>`** (`p` snippet, `i` media, `f` file),
   `Cache-Control: private, max-age=0`, no fragment in `Location` (the browser keeps the visitor's `#k=` across the
   redirect, RFC 9110 §10.2.2).
3. The redirect MUST NOT check the link key or the password (it never receives the fragment), MUST NOT render content,
   MUST NOT serve bytes, and MUST NOT set a cookie (spec MUST NOT #22).
4. `zaa.gl/s/...`, `zaa.gl/i/...`, `zaa.gl/f/...`, `zaa.gl/p/...` stay reserved and return `404` in Phase 1 (PD-02).
5. The viewer reads `#k=` with script, removes it from the visible URL (`history.replaceState`) and calls the content
   API through the BFF with the key in header `X-Zaagl-Link-Key` (§5). Because the viewer can be opened directly, the
   content API MUST re-run the full gate (§5); it never trusts that the visitor came through `zaa.gl`.
6. Crawler previews (Open Graph) are produced only for public content (no link key) that is `ready`, never for
   password-protected or burn content. The exact preview content is ARC-003/FE-024 work.
7. Which hop counts a "view", and whether link `MaxClicks` applies to content, is **PROPOSED (PD-10)**, not approved.
   Until decided, the redirect of a content link MUST NOT consume a click limit; this means `MaxClicks` has no effect on
   content links in Phase 1 until PD-10 is approved.

## 4. Hosts and headers

### 4.1 Host table (extends spec §1.2)
| Host | Serves | Cookies | Notes |
|---|---|---|---|
| `zaa.gl` | redirects and `404/410` pages, as spec §1.2; `302` to viewers for content links | **never** | Never renders or serves content. |
| `api.zaa.gl` | JSON API including content APIs and tus uploads (§6) | **never**, except the sign-in handshake cookies of §4.1.2 (PD-46) | Behind Cloudflare. Bearer tokens and API keys only; no CORS (spec §7.13). |
| `app.zaa.gl` | web app, BFF, viewers `/p/<code>`, `/i/<code>`, `/f/<code>`, `/activate` (§14) | host-only `__Host-` only | Renders user content only as inert data (§4.3). |
| `usercontent.zaa.gl` | user bytes: downloads, inline originals, previews, raw snippet text, all by ticket (§8) | **never** | PD-03. **Proxied by Cloudflare (PD-38).** DNS and TLS in OPS-001. |

#### 4.1.2 Sign-in handshake cookies on `api.zaa.gl` (PD-46, amends spec §1.2)
Spec §1.2 "`api.zaa.gl`: no cookies" is amended for the Google and GitHub sign-in routes only
(`/api/auth/external/{provider}`, `/api/auth/external/callback`, `/api/auth/signin-google`, `/api/auth/signin-github`):
1. Only those routes may set cookies, and only the cookies the OAuth handshake needs (the external sign-in cookie
   `.zaagl.external` and the provider correlation/nonce cookies, `src/Zaagl.Api/Program.cs:150-179`).
2. Each is host-only (no `Domain` attribute, so §4.2 rule 2 holds), `HttpOnly`, `Secure`, short-lived (at most
   10 minutes), and is removed when the callback completes.
3. No other `api.zaa.gl` route sets a cookie, and no `api.zaa.gl` route accepts a cookie as an API credential; the
   API stays bearer-only (spec §7.13).

#### 4.1.1 Delivery through Cloudflare (PD-38)
1. `usercontent.zaa.gl` is proxied by Cloudflare. Cloudflare Free limits each HTTP request and response, not the whole
   file, so **every response body from `usercontent.zaa.gl` MUST be at most `Content:MaxResponseBytes`**
   (to confirm; proposed 90,000,000 bytes, the same bound as upload requests, §6.1). Larger objects are delivered in
   parts: HTTP `Range` requests (`206`) or chunked download parts, each part under the limit.
2. Uploads stay chunked on `api.zaa.gl` (PD-12, §6).
3. Cloudflare MUST NOT cache any `usercontent.zaa.gl` response (origin sends `Cache-Control: private, no-store`, §4.4;
   a cache-bypass rule for the host is configured, OPS-001). Tickets appear in edge logs as paths; this is acceptable
   because they are short-lived and bound to one grant (§8.1).
4. In Phase 1 only Starter files between `Content:MaxResponseBytes` and 100 MB need more than one part. How the
   browser saves a whole file larger than one part (viewer fetches `Range` parts and assembles them, or the server
   offers numbered download parts) is **OPEN (ARC-008, FE)**. Until decided, `Content:MaxResponseBytes` is enforced and
   QA records whole-file downloads above it as NOT_TESTED.
5. Burn (§8.2): `Burn:MaxRequests` MUST stay at least the number of parts of the largest burnable file plus retries;
   with the Phase 1 sizes (at most 2 parts) the approved 16 is sufficient.
6. **Pre-launch check (§20):** confirm Cloudflare's self-serve terms on serving large non-web files through the CDN.
   The OPS-001 check found a conflict; the choice of delivery host is **PD-52 (DEFERRED** to before launch). Options
   other than accepting the risk change §4.1.1 (see ARC-008 §8.1).
7. An object above `Content:MaxResponseBytes` requested without one acceptable byte range gets
   `416 range-required` (with `Content-Range: bytes */<size>`), never a partial body the client did not ask for.

### 4.2 Mandatory mitigations for a same-site user-content host (PD-03)
`usercontent.zaa.gl` is same-site with `app.zaa.gl`. These rules are the condition of the approval; each has a test.
1. **No cookie on the user-content host.** No response from `usercontent.zaa.gl` carries `Set-Cookie`, from the
   application, Nginx or the CDN.
2. **No cookie on `.zaa.gl`.** No host of the zone may set a cookie with a `Domain` attribute of `zaa.gl` (or any
   cookie that `usercontent.zaa.gl` would receive). Edge features that set zone-wide cookies (for example Cloudflare
   bot-management or challenge cookies) MUST be verified absent or confined to their own host before launch (OPS).
3. **App cookies stay host-only.** Every `app.zaa.gl` cookie is named `__Host-…` (`Secure`, `Path=/`, no `Domain`)
   (spec §7.13).
4. **CSRF on every state change.** Every BFF route that changes state requires the double-submit CSRF token
   (`__Host-zaagl_csrf` cookie plus header) (spec §7.13). `SameSite` is not a defence between same-site hosts.
5. **Every response is inert.** Every `usercontent.zaa.gl` response carries either `Content-Disposition: attachment`
   or the sandbox CSP below, and in practice both rules of §4.4 apply to every response, including errors.
6. **No credentials accepted.** The host ignores `Authorization` and cookies; access is only by ticket (§8).
7. **Abuse exposure accepted.** Abuse on hosted files can hurt the reputation of `zaa.gl` itself (accepted risk). The
   abuse rules of §13 and monitoring of the zone's Safe Browsing / Web Risk status are mandatory at launch.

### 4.3 Viewer rules on `app.zaa.gl`
- User content is shown only as data: escaped code (syntax highlighting), Markdown without raw HTML (renderer location
  **OPEN, PD-09**), `<img>`/`<video>` elements whose `src` is a ticketed `usercontent.zaa.gl` URL, PDF preview with
  script evaluation off (PD-31, proposed). User HTML or SVG MUST NOT be inserted into the app's DOM as markup.
- **PD-23:** the BFF session of spec §7.13 (FE-029) MUST be in place before any Phase 1 content renders on
  `app.zaa.gl`. No access or refresh token is held by browser script or `localStorage` at that point.
- **PD-41:** that includes the two remaining places a sign-in token reaches page script today: social sign-in and live
  statistics (§4.5). Both MUST be changed before Stage 2 content goes live.

### 4.4 Response headers on `usercontent.zaa.gl`
| Header | Value |
|---|---|
| `X-Content-Type-Options` | `nosniff` (always) |
| `Content-Security-Policy` | `sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'` (always) |
| `Content-Disposition` | `attachment; filename="<ascii>"; filename*=UTF-8''<encoded>` for downloads, raw "download", SVG, HTML, XML, PDF and every type outside the inline allowlist; `inline` only for allowlisted raster images, video and derived previews |
| `Content-Type` | the **detected** type (§7.2); `application/octet-stream` for attachments of unknown type; `text/plain; charset=utf-8` for raw snippets. Never the client's declared type. |
| `Cache-Control` | `private, no-store` |
| `Referrer-Policy` | `no-referrer` |
| `Cross-Origin-Resource-Policy` | `cross-origin` (the viewer embeds media) |
| `X-Robots-Tag` | `noindex, nofollow` |

`Range` requests MUST be supported (206) for downloads and video.

### 4.5 No sign-in token in page script (PD-41)
Today the social sign-in callback redirects to `app.zaa.gl/auth/callback#token=…&refresh=…`
(`src/Zaagl.Api/Endpoints/IdentityEndpoints.cs:153`), and the statistics hub accepts the access token in the query string
`access_token` (`src/Zaagl.Api/Program.cs:118-123`), which page script must hold. Before Stage 2 content goes live:
1. **Social sign-in uses a one-time code.** After the provider callback the API redirects to the BFF with a one-time
   sign-in code, never with an access or refresh token in the URL (fragment or query). The BFF exchanges the code
   server-side with the API over the internal network for the token pair and stores it in `__Host-zaagl_session`
   (spec §7.13). The code MUST be single-use, short-lived (lifetime **(to confirm)**, proposed 60 seconds), stored only
   as a hash, and bound to the BFF flow that started the sign-in (a verifier the BFF keeps in a `__Host-` cookie and
   presents at exchange), so a code captured from a URL or log cannot be redeemed by another browser. A second
   redemption fails and MUST NOT issue tokens. Route names are fixed by the backend (BE) with ARC-016.
2. **Live statistics use a hub ticket.** The BFF obtains, server-side, a separate short-lived **hub ticket** for the
   signed-in user and gives only that ticket to page script. The ticket is accepted **only** for connecting to the
   statistics hub, never as a bearer token on any other endpoint; it is bound to one user, signed or stored hashed,
   single-use at connect, and short-lived (lifetime **(to confirm)**, proposed 60 seconds). The hub MUST NOT accept an
   access token in the query string once the ticket exists. An open hub connection MUST be closed when the user's
   session is revoked (security stamp change, spec §7.4) within the spec's 60-second stamp cache bound.
3. The provider round-trip cookies on `api.zaa.gl` are allowed only as §4.1.2 states (PD-46).

## 5. Access gate, link key and grants

### 5.1 Link key (PD-01)
1. A private content link has a 128-bit **link key**: 16 bytes from a CSPRNG, base64url without padding (22
   characters). The share URL is `https://zaa.gl/<code>#k=<key>`.
2. The server stores only `SHA-256(key)` and compares in constant time. The plain key is returned once, in the create
   response, and never logged, never stored, never put in a path or query string.
3. Defaults (PD-01, PD-37): **snippets, files and media (images, animations, SVG, recordings) are all created with a
   key** (`#k=`). The owner may make a **snippet or file** public (PD-01), an **image** public (PD-37), or a **video or
   screen recording** public (PD-45) by an explicit choice at creation or later; the link then works without a key and
   may get an Open Graph preview (§3.6).
   Whether a guest may choose public at creation is **(to confirm)**; guests cannot change it later (§11.9).
   Making a keyed link public later removes the key hash; making a public image private again issues a new key (the old
   URL without a key stops working). Both are owner actions through `PATCH /api/resources/{id}`.
4. Clients send the key in request header `X-Zaagl-Link-Key`. A missing or wrong key on a keyed link MUST produce the
   same response as a code that does not exist (`404`), with the same timing class.
5. The key is a second factor for an unguessable URL, not encryption; client-side encryption is Phase 2 (PD-29,
   proposed).

### 5.2 Gate order (`IAccessGate`, contract C4)
Every content endpoint (descriptor, snippet text, raw, preview, ticket mint, unlock) evaluates, in order:
1. link state (spec §4.3) and resource state (§2.4);
2. link key, if the link has one (fail → `404`);
3. password, if the link has one: a valid grant (§5.3) for this link and purpose, else `401 password-required`.

The gate never consumes clicks, views or burns. Time comes from `TimeProvider`.

### 5.3 Access grants
- `POST /api/public/content/{code}/unlock { password }` (key header required for keyed links) returns
  `{ grant, expiresAt }`. Rate limit: the existing `unlock` policy per IP partition.
- A grant is an HMAC-SHA256 token with a `kid` from a rotating key set, bound to **one link id, its resource id, a
  purpose set and an expiry** (default 10 minutes). Clients send it in header `X-Zaagl-Grant`.
- A grant for one link MUST be refused for any other link; a grant MUST NOT widen its purposes; an expired or tampered
  grant → `401 password-required`.
- A password on content needs capability `PasswordOnContent`: **Starter only** (PD-54); Free and guests get
  `402 plan_required`. Unlock on a link without a password → `400 validation`.

## 6. Upload protocol (PD-12)

1. Uploads use **tus 1.0.0** on **`api.zaa.gl`**, through Cloudflare. Every request body MUST be **at most 90,000,000
   bytes** (`413 chunk-too-large` otherwise); clients default to 64 MiB chunks.
2. `POST /api/uploads` (create) requires `Upload-Length` (deferred length is refused, `400 upload-length-required`) and
   `Upload-Metadata` keys `filename`, `filetype`, `kind` (`file` or `media`); all metadata is untrusted display data.
   Before accepting any byte: `Upload-Length` > plan `MaxFileBytes` → `413 file-too-large`; quota reservation fails →
   `403 quota-exceeded`; disk admission fails → `507` with `Retry-After` (DEVOPS_PLAN §2.4). Guests: §11.
3. `HEAD`/`PATCH`/`DELETE /api/uploads/{id}`: the session id is a capability **and** the principal must match the
   creator (owner id or guest creator key); otherwise `404`. `409 upload-offset-mismatch` on a wrong offset.
4. Sessions idle for 24 hours expire: temp bytes deleted, reservation released, `410 upload-expired` afterwards.
5. `POST /api/uploads/{id}/complete` with `Idempotency-Key` sets title, expiry, password, burn, alias (allowed for
   every caller, guests included, PD-57) and public/keyed,
   creates the resource and link (§2.2) and answers `202 { resourceId, code, shortUrl, linkKey?, state: "processing",
   expiresAt }`. Replays with the same key return the same body; a different body → `422 idempotency-mismatch`.
6. **PD-28 (APPROVED):** the link is returned at completion and may be shared while the scan runs; the viewer shows
   "processing" and no bytes are served before the resource is `ready` (§2.4).
6a. **PD-15 (APPROVED):** uploaded bytes are stored on the server's own disk behind the storage interface (C7);
   S3/R2 may be added later as another implementation.
6b. **Custom aliases on content (PD-57, APPROVED):** snippets, files and media may get an alias (`customSlug`) from any
   caller, guests included, independent of the URL-link plan flag; Phase 0 alias syntax and reservation rules apply;
   guest aliases follow the spec §7.12 guest rules (CAPTCHA, guest counters) and are released at expiry.
7. Originals are stored byte-for-byte; nothing in Phase 1 re-encodes an original. Whether location metadata is
   stripped on request is **OPEN (PD-13)**.
8. A simple single-request upload for small files (`POST /api/uploads/simple`) is allowed only within the same 90 MB
   request limit and the same rules.

## 7. Scanning and type detection (fail-closed)

### 7.1 Scanning
1. Every file and media upload MUST be scanned by ClamAV before it can become `ready`.
2. `clean` → promote to `objects/`, mark `ready`. `infected` → move to `quarantine/`, mark `rejected/infected`, keep for
   `Abuse:EvidenceRetentionDays`, take the link down with kind `safety` (§13).
3. Engine error, timeout or unavailability → retry with backoff (1, 5, 15, 60 minutes; at most 6 attempts), then
   `rejected/scan-unavailable`. **Fail-closed:** an unscanned resource is never served to anyone, the owner included.
   The schedule and attempt count are configuration (`Scan:RetryMinutes`, `Scan:MaxAttempts`). Because a scanner outage
   of a few hours rejects the uploads made during it, the owner MUST see a distinct "scanning failed, please upload
   again" status for `scan-unavailable`, not the malware message.
4. Phase 1 files are at most 100 MB (§10), below ClamAV's size limits; clamd MUST be configured so a 100 MB file is
   scanned completely (stream or path scan), and "too large to scan" never occurs in Phase 1.
5. Encrypted (unscannable) archives and executables: **OPEN (PD-16)**.

### 7.2 Type detection
- The stored content type comes from content sniffing of the bytes (first 64 KB), never from the declared type or the
  filename. The SHA-256 and size are computed from the stored bytes.
- The media format allowlist and re-kinding of mismatched "media" uploads are **OPEN (PD-16)**. SVG handling is PD-18
  (proposed: original only as attachment, viewer shows a rasterised preview).
- Filenames are sanitised (path parts, control and bidi characters, trailing dots/spaces, reserved device names, NFC,
  ≤ 255 bytes) and used only as display metadata, never in a storage path.
- A SHA-256 on the content hash blocklist → `rejected/blocked-hash` (§13).

## 8. Delivery tickets and Burn After Downloading

### 8.1 Tickets
1. All bytes on `usercontent.zaa.gl` are reached only through `https://usercontent.zaa.gl/d/<ticket>`. A ticket is an
   HMAC-SHA256 token with `kid` (rotating key set `Content:TicketKeys`) binding a grant id, resource id, purpose
   (`download`, `inline`, `preview`, `raw`, `owner`), optional derivative, and expiry.
2. **Download** tickets are minted only by `POST /api/public/content/{code}/download` (after the gate, §5) and expire
   **120 seconds** after minting if unused. The viewer descriptor MUST NOT contain a download ticket.
3. **Inline / preview / raw** tickets may appear in the descriptor; they never consume burn and are never minted for
   burn files. Their lifetime is configuration (proposed 15 minutes).
4. Owner tickets (`POST /api/resources/{id}/download`) never consume burn.
5. `/d/<ticket>`: the API authorizes the ticket (signature, kid, expiry, grant, resource state `ready`) and answers with
   `X-Accel-Redirect` to an internal Nginx location; Nginx serves bytes with the §4.4 headers and `Range`. A forged,
   expired, or other-resource ticket → `403 ticket-invalid`; burned → `410 burned`; any other closed state →
   `410 gone` (the API routes give the specific code); not ready → `409`; rejected → `422`; above the response cap
   without an acceptable range → `416 range-required` (§4.1.1.7).
6. Lifetimes are technical defaults, not product decisions, and MUST be configuration: unused download ticket 120 s
   (`Content:DownloadTicketSeconds`), inline/preview/raw tickets 15 min (`Content:InlineTicketMinutes`), access grants
   10 min (`Access:GrantMinutes`). Tests read them from configuration.
7. Each response is bounded by `Content:MaxResponseBytes` (§4.1.1, PD-38).

### 8.2 Burn After Downloading (PD-05)
1. A burn file is **consumed by the first GET of a download ticket** minted by the download button's POST.
2. The claim is one atomic conditional `UPDATE` (state `ready`, not claimed, not expired/deleted/disabled). Under any
   concurrency **exactly one** request wins; every other first GET gets `410 burned`.
3. The winning grant may serve further requests (resume with `Range`) for **30 minutes after the claim, at most 16
   requests in total** (`Burn:GraceMinutes`, `Burn:MaxRequests`). After that the bytes are deleted and the quota
   released.
4. **Never consumes:** the `zaa.gl` redirect, the viewer page, the descriptor, `HEAD` requests, previews and
   thumbnails, owner downloads. Burn files get **no previews or inline tickets**. Burn MUST NOT depend on the
   User-Agent (preview bots never POST, so they never get a download ticket).
5. A password-protected burn file requires a valid grant to mint the ticket.
6. Burn applies to **files** in Phase 1. "Burn after view" for snippets is **OPEN** (PD-05 note). Optional CAPTCHA on
   the burn POST and an owner email on burn are **OPEN**. Which plans may create burn files: capability flag
   `BurnAfterDownload`, **Starter only** (PD-54).
7. After the claim the link answers `410` ("downloaded and deleted") and cannot be renewed; the owner cannot switch burn
   off after a claim (`409 burn-already-claimed`).

## 9. Retention and expiry (PD-06)

| | Guest | Free | Starter |
|---|---|---|---|
| Default and maximum content expiry | 7 days | 7 days | 30 days |
| Quarantine after expiry | none | 90 days (`Links:QuarantineDays`) | 90 days |
| Bytes deleted at | `expires_at` | `purge_after` | `purge_after` |
| Renew during quarantine | ✗ | ✓ (up to plan maximum from now) | ✓ |

1. A content link MUST always expire: `expires_at = min(requested, now + plan RetentionDays)`; a later request →
   `400 expiry-beyond-plan`. Durations are fixed days in UTC (spec §1.1).
2. **Account content:** at `expires_at` the link is `quarantined` (`410`) and the bytes are **kept**, unreachable, until
   `purge_after`, so the owner can renew. Renewal follows spec §7.4 (only forward, up to the plan maximum from now) and
   moves `purge_after`; bytes stay. At `purge_after` the bytes are deleted and the resource is purged.
3. **Guest content:** `purge_after = expires_at`; at expiry the link answers `404` and the bytes are deleted.
4. Burn (§8.2) and takedown (§13) override this table.
5. Owner deletion (PD-55, APPROVED): the link is `deleted` (`410`, not renewable). The bytes become unreachable
   immediately but are **kept through the 90-day quarantine** and deleted at `purge_after`; the owner's storage is freed
   only at purge. Whether a takedown frees the owner's storage at once or when the legal hold ends is **OPEN**
   (ARC-006 Q2).
6. Deletion of bytes MUST be on time and crash-safe (deletion outbox); "expired bytes still on disk more than 15 minutes
   after their deletion time" is an alert. Disk sizing includes the 90-day quarantine.
7. A downgrade from Starter to Free does not shorten existing content; new content uses the new plan. **(to confirm,
   ARC-005)**
8. Separately from content, the Phase 0 conflict "spec says every link expires, code allows no expiry" remains a
   Phase 0 decision outside this addendum.

## 10. Plans, limits and quotas (PD-04)

| | Guest | Free | Starter |
|---|---|---|---|
| Max file or media size | 25 MB (PD-54) | 25 MB | 100 MB |
| Content retention | 7 days | 7 days | 30 days |
| Per-account storage total | n/a (creation limits, §11) | 250 MB (PD-54) | 5 GB (PD-54) |
| Snippet size per version | 256 KB (PD-56) | 512 KB (PD-56) | 2 MB (PD-56) |
| Burn after download, password on content | ✗ | ✗ | ✓ (PD-54) |
| Custom alias on content | ✓ (PD-57) | ✓ (PD-57) | ✓ (PD-57) |
| API keys | ✗ | ✗ | ✓ (PD-07) |
| Device sign-in (CLI, desktop) | ✗ | ✓ | ✓ |

0. **Units (PD-54):** 1 MB = 1,048,576 bytes (likewise 1 KB = 1,024, 1 GB = 1,073,741,824). Every value is
   configuration; tests read it from configuration.
1. **There is no Pro tier in Phase 1.** No 5 GB uploads; the PRD "3% free-to-Pro" exit gate cannot be measured.
2. Code outside Billing MUST NOT compare plan tiers (`Tier == Starter`); it reads capability flags (`IsPaid`, `ApiKeys`,
   `KeepStatsAfterPurge`, `BurnAfterDownload`, `PasswordOnContent`, `ContentStatistics`, `ManageContent`) and
   `ContentLimits` from `IContentPlanProvider` (contract C5). Values come from configuration.
3. Quota is a durable ledger (`used_bytes`, `reserved_bytes`) per owner, changed only in transactions holding the
   owner's advisory lock: reserve at upload create (declared length), commit at `ready` (actual size), release on abort,
   rejection, expiry-deletion, burn and delete. `used + reserved` MUST never exceed the quota and never go negative
   under any concurrency (race test).
4. Quota and limit checks run after the idempotency claim (spec MUST NOT #8) and inside the transaction (spec §7.2
   step 9).
5. Whether content shares count toward Free's 50 links per month is **PROPOSED (PD-08: no)**, not approved.
6. `GET /api/me/storage` returns plan limits, used, reserved and quota.

## 11. Guest content (PD-14, as updated 2026-11-09)

1. Requests without credentials may create **snippets and uploads** (files and media). Requests with invalid, revoked or
   expired credentials are never treated as guests (spec MUST NOT #27, PD-36, §19).
2. **CAPTCHA:** Turnstile token in `Captcha-Token`, verified as spec §7.12 (hostname, action, age), on snippet create and
   on upload-session create. Actions: `guest-snippet`, `guest-upload` (names fixed by BE-026).
3. **Rate limits, per IP partition** (IPv4 address, IPv6 /64, spec §7.3): **30 creations per hour and 60 per rolling
   24 hours**, snippets and uploads **counted together**, enforced as hard limits inside the creation transaction with
   locked hourly buckets like spec §7.12 (`429 quota`). An upload counts at session create.
4. Wider caps for one IPv6 allocation: per /56 and per /48, **proportional, values to confirm** (proposal, keeping spec
   §7.12's ratios: /56 = 5×, /48 = 15× the /64 limits). Whether the global guest ceiling of spec §7.12 (written for
   URL links) also covers guest content is **OPEN**, decided together with item 5.
5. Whether guest URL links (spec §7.12, 20 per 24 h) share this budget is **OPEN**; until decided the two counters are
   separate.
6. **Enforceability:** these limits depend on the client IP being trustworthy. They MUST NOT be enabled in an
   environment until **BE-030** (forwarded headers trusted only from the exact known proxies, spec §7.13, MUST NOT
   #29) is deployed there; QA records them BLOCKED before that.
7. Expiry: default and maximum **7 days**; **no quarantine**: at expiry the link answers `404` and bytes are deleted.
8. Per-file cap: **25 MB** (PD-54). Guest snippet size: **256 KB** (PD-56).
9. **No statistics** (only the aggregate counter of spec §7.3), **no management** (no renewal, edit, delete, list),
   **no emails**. Admins can view and disable guest content.
10. Guest content is keyed like account content (§5.1, PD-37). No passwords and no burn for guests (PD-54: Starter
    only). Guests may choose a custom alias (PD-57).
11. Guest uploads are scanned and served exactly like account uploads (§7, §8).

## 12. Analytics event kinds

| Kind | Name | Recorded when |
|---|---|---|
| 0 | `redirect` | as Phase 0 (`302` from `zaa.gl`) |
| 1 | `view` | the content descriptor is served to a non-bot |
| 2 | `raw` | raw snippet text is served |
| 3 | `download` | a download ticket's first authorized GET |

1. `click_events` and `link_daily_stats` gain `kind smallint not null default 0` (expand-only; indexes per partition).
   URL links' dashboards MUST show identical numbers after the change.
2. Counts used for burn or billing come only from the durable grant table, never from the buffered pipeline (spec
   §7.5: analytics are best effort).
3. Guest content records no events (spec MUST NOT #26). No IP-derived data is stored.
4. The headline "views" definition and de-duplication are **PROPOSED (PD-10)**, not approved. Until PD-10 is approved
   no content event (kinds 1-3) is written; the owner's download count comes from the grant table only.

## 13. Abuse and takedown for content

1. Reports (`POST /api/reports`, spec §7.15) name a code and therefore cover content links. Viewer pages link to the
   report form.
2. Takedown of a content link reuses the spec §7.15 routine (disable with `abuse` or `safety`, block the alias, write
   evidence, notify the cache) and additionally **moves the bytes to `quarantine/` with a legal hold**
   (`legal_hold_until = now + Abuse:EvidenceRetentionDays`; the duration is PD-11, OPEN) instead of deleting them. Evidence rows gain `resource_id`,
   `sha256` and `object_key`.
3. After a takedown no ticket for that resource may be minted or honoured; bytes become unreachable on every instance
   within the spec §7.3 invalidation bound (15 s on a stalled instance).
4. Content hash blocklist (`files.blocked_content_hashes`): checked at finalize; adding a hash takes down live resources
   with that hash. Bulk takedown gains filters by `sha256` and `target_kind`.
5. The Web Risk re-check of spec §7.15 MUST skip content kinds (no destination URL). Web Risk for URLs inside snippets is
   not required in Phase 1.
6. Response targets of spec §7.15 apply (12 h phishing and malware, 48 h otherwise).
7. EU legal duties (DSA notice-and-action, CSAM hash matching, evidence retention, backups) are **OPEN (PD-11, counsel)**
   and block public launch, not development.

## 14. CLI and desktop authentication and scopes (PD-07, PD-19)

1. The CLI and desktop app sign in with the **OAuth 2.0 Device Authorization Grant** (RFC 8628) for **every account
   plan**: `POST /api/auth/device/code`, user approval on `https://app.zaa.gl/activate`, polling
   `POST /api/auth/device/token` (`authorization_pending`, `slow_down`, `access_denied`, `expired_token`). User codes
   are single-use and expire after 10 minutes. No client secret ships in any binary.
2. Native-client tokens MUST NOT carry the Admin role and are excluded from the "Interactive" policy (API-key management,
   billing, account deletion and export). Refresh rotation and reuse detection follow spec §7.4; each device session is
   listed and revocable.
3. **API keys stay paid-only** (capability `ApiKeys`), for automation and CI. Keys stop working when the owner's plan
   loses the capability (spec §7.4).
4. **Scopes are enforced** on every endpoint (BE-001): a key without the scope gets `403 insufficient-scope`. Phase 1
   scopes: existing `links:read`, `links:write`, `stats:read`, plus `snippets:write`, `files:write`, `resources:read`.
   JWT and device sessions hold all non-admin scopes.
5. Desktop global shortcut default: **Ctrl+Alt+Z** (Windows) and **Cmd+Ctrl+Z** (macOS), rebindable (PD-19). This
   differs from the PRD; product copy MUST match.
6. Loopback + PKCE sign-in for desktop is a later P2 upgrade. Headless CLI credentials, minimum OS versions and
   telemetry are **OPEN (PD-33)**.

## 15. Phase 0 fixes that Phase 1 depends on (PD-24)

Only these are fixed in Stage 1: **BE-001** (API-key scope enforcement), **BE-030** (trust `X-Forwarded-For` only from
exact known proxies), **BE-031** (redirect rate limits; `404` for 1–3 character and multi-segment paths before I/O).
Test isolation (QA-001..003) is deferred; flaky failures caused by the shared test database MUST be recorded by QA, not
ignored or retried until green.

## 16. You MUST NOT (Phase 1, in addition to spec §11)

1. Serve any content byte from `zaa.gl`, `api.zaa.gl` or `app.zaa.gl`; render user content as markup in an app origin.
2. Set a cookie on `usercontent.zaa.gl`, or a cookie with `Domain=zaa.gl` from any host.
3. Serve a `usercontent.zaa.gl` response without `nosniff` and the sandbox CSP, or a non-allowlisted type without
   `Content-Disposition: attachment`.
4. Use the client's declared MIME type or filename for a security decision, a `Content-Type` header or a storage path.
5. Serve bytes of a resource that is not `ready`; serve unscanned bytes; pass a resource when the scanner fails.
6. Put a download ticket in the viewer descriptor; mint a download ticket on a `GET`; consume burn on `HEAD`, preview,
   viewer, descriptor or owner download; decide burn by User-Agent; let two requests both win a burn claim.
7. Put a link key in a path, query string, log, analytics event, outbox row or database column other than its hash;
   distinguish "wrong key" from "no such code" in status or body.
8. Accept a grant or ticket for a different link, resource or purpose, or after its expiry.
9. Create a content link outside `ILinkIssuer`, or run URL-only steps (normalisation, Web Risk, blocklist, routing, UTM)
   on a content link.
10. Create content without an expiry, or with an expiry beyond the plan's retention; renew a burned, deleted or purged
    content link.
11. Delete account content bytes before `purge_after` because of expiry alone, or keep guest bytes after `expires_at`.
12. Accept a request body over 90,000,000 bytes on `api.zaa.gl`, accept an upload without `Upload-Length`, or write a
    byte before the plan size and quota checks pass.
13. Let `used + reserved` exceed a quota or go negative.
14. Compare `PlanTier` values outside Billing; add a Pro tier in Phase 1.
15. Enable guest content limits as a security control before BE-030 is deployed; store a guest's IP address.
16. Record statistics or raw events for guest content.
17. Issue a native-client token with the Admin role; let an API key act beyond its scopes; issue API keys on Free.
18. Delete taken-down bytes before their legal hold ends; take down content without an evidence row.
19. Re-encode or otherwise modify an original upload.
20. Implement behaviour for a topic marked OPEN or PROPOSED in this addendum without a recorded decision.
21. Treat a request that carries a failing credential as a guest, on any create route or in the BFF (§19).
22. Redirect a closed short link to the app instead of answering from `zaa.gl` (§17); send a disabled, deleted,
    purgeable or click-limit-exhausted link, or any content link, to a fallback URL; accept a fallback URL that fails
    the destination checks.
23. Let a non-owner, an admin included, read or change another user's link or content through an owner route (§18).
24. Put an access or refresh token in a URL (fragment or query) or hand one to page script, once Stage 2 content is
    live; accept a hub ticket anywhere but the statistics hub (§4.5).
25. Send a `usercontent.zaa.gl` response body larger than `Content:MaxResponseBytes`, or let Cloudflare cache one
    (§4.1.1).
26. Set a cookie on `api.zaa.gl` outside the sign-in handshake routes of §4.1.2.
27. Reveal the destination of a click-limited link, or consume its click, on `HEAD` (§17.6).

## 17. Short-link answers for closed links (PD-39, PD-42)

Amends the Phase 0 behaviour of redirecting closed links to the app (`src/Zaagl.Api/Endpoints/ResolutionEndpoints.cs:48-49`,
`src/Modules/Access/Application/AccessGate.cs:36`), restoring spec §7.3. Applies to `GET`/`HEAD zaa.gl/<code>` for URL
and content links.

| Link state (spec §4.3) | URL link | Content link |
|---|---|---|
| `purgeable` (includes an expired guest link) | `404` page | `404` page |
| `deleted` | `410` page | `410` page |
| `disabled` (`user-disabled`, owner, admin) | `410` "disabled" page | `410` "disabled" page |
| `disabled` (`abuse`, `safety`) | `410` acceptable-use page linking to `Links:ReportPageUrl` | same |
| `quarantined` (expired), owner set a fallback URL | **`302` to the fallback URL** | n/a (content links have no fallback, §2.1.2) |
| `quarantined` (expired), no fallback URL | `410` "expired" page | `410` "expired" page |
| `active`, `MaxClicks` budget used up (PD-43) | `410` "This link has reached its click limit" page, never the fallback | n/a (§3.7) |
| `HEAD` on an `active` link with `MaxClicks` set (PD-47) | `410`, no body, no `Location`, nothing consumed | n/a |
| `active` | `302` to the destination (Phase 0) | `302` to the viewer (§3.2) |

1. The pages are the small static HTML pages of spec §7.3, served by `zaa.gl` itself: no external assets,
   `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`, `nosniff`, `Referrer-Policy: no-referrer`,
   no cookie. `HEAD` returns the same status without a body. Cache TTLs follow spec §7.3 (`404` 10 s, `410` 60 s, never
   past `purge_after`).
2. **Fallback (PD-42):** only an **expired** (`quarantined`) link uses its fallback URL. **Disabled and deleted links
   always get the `410` page, never the fallback**; `purgeable` links always `404`. The fallback `302` carries
   `Cache-Control: private, max-age=0` (never `301`) and counts toward the tiered miss limits of spec §7.3 like the `410`
   it replaces.
3. **PD-44:** the fallback URL gets the same checks as a destination: the URL rules, the destination blocklist and the
   Web Risk reputation check of spec §7.2 step 5 when it is set or changed, and the Web Risk re-check and blocklist
   takedown of spec §7.15 afterwards, exactly as for `target_url`. A fallback that fails is refused at set time like a
   destination. (Phase 0 code checks only URL syntax, `src/Modules/Links/Application/LinkService.cs:914-918`; BE closes
   the gap.)
4. Whether a fallback `302` counts as a click or records analytics is **OPEN**; until decided it consumes nothing and
   records nothing.
5. **PD-43:** a URL link whose `MaxClicks` budget is used up gets the small static page "This link has reached its click
   limit" from `zaa.gl` with status `410` (same headers as item 1), **never the fallback URL**. This replaces the Phase 0
   behaviour of sending it to the fallback or the app's "expired" page
   (`src/Modules/Resolution/Application/ResolutionService.cs:109`). It counts toward the miss limits like any `410`.
6. **PD-47:** `HEAD` on a URL link that has a click limit (`MaxClicks` set), whether or not its budget is used up,
   answers `410`, the click-limit page status, **without a body and without `Location`**: it never reveals the
   destination and never consumes a click. This replaces the Phase 0 behaviour of reporting the destination on `HEAD`
   (`src/Modules/Resolution/Application/ResolutionService.cs:77-80`). Links without a click limit are unaffected:
   `HEAD` answers like `GET` (same status and `Location`) without counting (spec §7.3).
6. Password-protected active URL links keep the Phase 0 redirect to the app's unlock page (not covered by PD-39).
7. Preview crawlers get the same page as a browser for a closed link, never Open Graph tags.

## 18. Owner routes and admin routes (PD-40)

Amends spec §7.4 "Authorization".
1. The owner routes `GET /api/links`, `GET/PATCH/DELETE /api/links/{code}`, `POST /api/links/{code}/renew`,
   `GET /api/links/{code}/stats` and `/api/archive*` are for the **owner only**. The words "or Admin" in those rows of the
   spec §7.4 table, and "owner or admin" in renewal step 2, are removed.
2. Any other principal, **an admin included**, gets `404` for another user's link on an owner route, with the same body
   and timing class as an unknown code. An admin is the owner of their own links like any user.
3. Admins act on other users' links only through `/api/admin/*` (role `Admin`, JWT with TOTP, `Admin:AllowedNetworks`,
   spec §7.4), and only with the actions those routes offer. Adding admin actions (for example admin renewal) is a
   product decision, not covered here.
4. The same rule applies to the Phase 1 owner routes (`/api/resources*`, `/api/snippets/{id}*`, `/api/me/storage`,
   C9): owner only, `404` for everyone else; admin content actions (view, disable, takedown, §13) are under
   `/api/admin/*`.
5. QA's generated authorization tests (spec §7.4) expect `404` for "admin JWT with TOTP" on another user's link or
   content through an owner route (QA's SpecGap §7.4 cases change accordingly).

## 19. Failing credentials are never guests (PD-36)

1. On every route that accepts guests (`POST /api/links`, `POST /api/snippets`, `POST /api/uploads`,
   `POST /api/uploads/simple`), a request is a guest **only if it carries no credential at all**.
2. A request that carries a credential that fails, for any reason (malformed, wrong, revoked or expired API key or
   token, bad signature, superseded security stamp, an API key whose owner lost the `ApiKeys` capability, an enrol-only
   token), MUST get `401` with `WWW-Authenticate`. The guest path MUST NOT run: no CAPTCHA verification, no guest counter
   consumed, no code or alias reserved, no link or resource created.
3. A valid credential without the needed scope gets `403 insufficient-scope` (§14.4), also never guest.
4. The BFF MUST NOT turn a browser request whose session cookie is present but cannot be refreshed into a
   credential-less (guest) API call; it answers `401` and the app asks the user to sign in again.
5. Spec §7.2 step 1 and MUST NOT #27; fixed for `POST /api/links` in Stage 1 (backend), and binding for the Phase 1
   create routes when they are built.

## 20. Pre-launch checks

Not needed for development; each MUST be done and recorded before public launch (Stage 7).
1. **Cloudflare terms (PD-38):** confirm Cloudflare's self-serve terms allow serving large non-web files (downloads,
   video, archives) through the CDN on `usercontent.zaa.gl`. If not, the product owner decides again on the delivery
   host. Owner: OPS-001 / ARC-008.
2. **Edge cookies (§4.2.2):** verify no Cloudflare feature (bot management, challenge, Turnstile) sets a cookie on
   `usercontent.zaa.gl` or with `Domain=zaa.gl`. Owner: OPS-001 / OPS-010 acceptance.
3. **Zone reputation (§4.2.7):** Safe Browsing / Web Risk monitoring of `zaa.gl` and its subdomains is live.
4. **Legal (PD-11, OPEN):** DSA notice-and-action, CSAM hash matching, evidence retention and backups reviewed by
   counsel.

## Appendix A. Decision map

| Decision | Status | Sections |
|---|---|---|
| PD-01 link key `#k=` | APPROVED | §5.1 |
| PD-02 viewers on `app.zaa.gl/p|i|f` | APPROVED | §3, §4 |
| PD-03 user bytes on a `zaa.gl` subdomain | APPROVED (changed) | §4 |
| PD-04 Free and Starter only, capability flags | APPROVED (changed) | §10 |
| PD-05 burn definition | APPROVED | §8.2 |
| PD-06 bytes kept through quarantine, guest deleted at expiry | APPROVED (changed) | §9 |
| PD-07 device authorization, paid API keys | APPROVED | §14 |
| PD-12 tus on `api.zaa.gl`, ≤ 90 MB requests | APPROVED | §6 |
| PD-14 guest snippets and uploads, 30/h and 60/day | APPROVED (changed, updated) | §11 |
| PD-15 server disk storage behind the storage interface | APPROVED | §6.6a |
| PD-19 Ctrl+Alt+Z / Cmd+Ctrl+Z | APPROVED (changed) | §14 |
| PD-23 BFF session before content | APPROVED | §4.3 |
| PD-24 BE-001, BE-030, BE-031 only | APPROVED (partial) | §15 |
| PD-25 background jobs on PostgreSQL task tables in the worker | APPROVED | (technical; ARC-012) |
| PD-28 link shareable while scanning, no bytes before `ready` | APPROVED | §6.6 |
| PD-34 this addendum is the Phase 1 rulebook | APPROVED | header |
| PD-36 failing credential → `401`, never guest | APPROVED | §11.1, §19 |
| PD-37 images private by default, owner may make public | APPROVED | §5.1 |
| PD-38 `usercontent.zaa.gl` proxied by Cloudflare, per-request bounded delivery | APPROVED | §4.1, §4.1.1, §20 |
| PD-39 closed short links answered by `zaa.gl` with `410`/`404` pages | APPROVED | §3.2, §17 |
| PD-40 admins only through `/api/admin/*`; spec §7.4 amended | APPROVED | §18 |
| PD-41 one-time sign-in code, hub ticket | APPROVED | §4.3, §4.5 |
| PD-42 expired → fallback `302` if set, else `410`; disabled/deleted always `410` | APPROVED | §17 |
| PD-43 click limit used up → `410` click-limit page, never the fallback | APPROVED | §17.5 |
| PD-44 fallback URLs get destination checks (blocklist, Web Risk) | APPROVED | §17.3 |
| PD-45 video and recordings private by default, owner may make public | APPROVED | §5.1.3 |
| PD-46 `api.zaa.gl` cookies only on Google/GitHub sign-in routes; spec §1.2 amended | APPROVED | §4.1.2 |
| PD-47 `HEAD` on a click-limited link: `410`, no body, no destination, no click | APPROVED | §17.6 |
| PD-09, 11, 13, 16, 33 | OPEN | marked in place |
| PD-52 Cloudflare terms vs PD-38 | DEFERRED (before launch) | §4.1.1.6, §20 |
| PD-53 app CSP, mixed nonce/hash approach (ADR-0023) | APPROVED | §4.3 (ADR-0023) |
| PD-54 storage totals, guest file cap, burn and password Starter only, 1 MB = 1,048,576 bytes | APPROVED | §5.3, §8.2.6, §10, §11 |
| PD-55 owner delete keeps bytes until `purge_after` | APPROVED | §9.5 |
| PD-56 snippet caps 256 KB / 512 KB / 2 MB | APPROVED | §10, §11.8 |
| PD-57 custom aliases on content for everyone, guests included | APPROVED | §6.6b, §10, §11.10 |
| PD-08, 10, 17 (version limit and diff scope only), 18, 29, 31 | PROPOSED (not approved; treated as OPEN) | marked in place |

Other OPEN items without a PD number, each marked in place: browser assembly of downloads above one part (§4.1.1.4);
burn after view for snippets, CAPTCHA on burn, owner email on burn (§8.2.6); guest URL links sharing the content budget
and the global guest ceiling (§11.4–11.5); fallback `302` as a click (§17.4); route prefix (§1, PD-33).

Values still **(to confirm)**: guest public choice, IPv6 /56 and /48 guest caps, downgrade effect,
`Content:MaxResponseBytes`, sign-in code and hub ticket lifetimes. Still **OPEN**: storage release at takedown
(§9.5).

## Appendix B. Version history

| Version | Date | Change |
|---|---|---|
| 1.0 | 2026-11-09 | Draft for QA freeze (ARC-001). |
| 1.1 | 2026-11-09 | Approved as the Phase 1 rulebook (PD-34). Added PD-36 (§19), PD-37 (§5.1.3), PD-38 (§4.1.1, §20), PD-39 and PD-42 (§17), PD-40 (§18), PD-41 (§4.5); MUST NOT 21–25; PD-08 and PD-10 relabelled PROPOSED to match DECISIONS.md; ticket and scan timings made configuration (§7.1.3, §8.1.6); global guest ceiling for content marked OPEN (§11.4); pre-launch checks (§20). Head's review: `reviews/stage1-architect.md`. |
| 1.2 | 2026-11-09 | PD-43 (§17.5 click-limit page), PD-44 (§17.3 fallback checks), PD-45 (§5.1.3 video and recordings), PD-46 (§4.1.2, spec §1.2 amended); MUST NOT 22 widened, 26 added. Browser assembly of multi-part downloads stays OPEN. |
| 1.3 | 2026-11-09 | PD-47 (§17.6 `HEAD` on click-limited links); MUST NOT 27. |
| 1.4 | 2026-11-09 | Product-owner approvals PD-15, PD-25, PD-28, PD-53 to PD-57 applied (§5.3, §6.5-6.6b, §8.1.5, §8.2.6, §9.5, §10, §11.8, §11.10, §12.4, §13.2); `416 range-required` and `410 gone` on `/d/` (§4.1.1.7, §8.1.5); PD-52 noted (§4.1.1.6). Head's review: `reviews/stage1-round3-architect.md`. |
