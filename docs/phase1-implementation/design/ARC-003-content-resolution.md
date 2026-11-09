# ARC-003 · Resolution flow for content kinds (snippets first)

| | |
|---|---|
| Owner | zaagl-architect. Implementers: backend (Resolution is built; BE content endpoints, delivery hop), frontend (viewer `/p/[slug]`), CLI (`zaagl get`). |
| Status | DESIGN for Stage 2. The `zaa.gl` part is **built** (Stage 1, ARC-002 §7); this note fixes the rest of the path and the snippet specifics. |
| Rules | Addendum §2.4, §3, §4.3-4.4, §5, §9, §11, §12, §17. PD-01, PD-02, PD-06, PD-14, PD-37, PD-39, PD-42, PD-47, PD-48, PD-53, PD-55 (APPROVED). ADR-0001, 0002, 0003, 0016, 0018, 0023. |
| Related | ARC-002 (link target), ARC-004 (gate, grants), ARC-006 (resource state), ARC-008 (delivery host, tickets), ARC-010 (snippet model, rendering), `openapi-phase1.yaml`. |
| Not decided here | Which hop counts a "view" (PD-10, PROPOSED): until approved no content event of kind 1-3 is written (addendum §12.4). Burn after view for snippets (separate yes/no in PD-05): not built. |

## 1. Hops and what each one may do

| Hop | Host | Sees the `#k=` key? | Checks | Serves | Cookies |
|---|---|---|---|---|---|
| H1 short link | `zaa.gl/<code>` | no (fragment never sent) | path shape, rate limits, **link state only** | `302` to the viewer, or static `404`/`410` page | never |
| H2 viewer page | `app.zaa.gl/p/<code>` | yes, in page script only | none itself (page shell); SSR asks the descriptor **without** a key for crawler meta (§5) | HTML shell, nonce CSP (PD-53) | host-only `__Host-*` (BFF) |
| H3 content API | `api.zaa.gl/api/public/content/{code}*` (browser reaches it through `app.zaa.gl/bff/api/...`) | yes, header `X-Zaagl-Link-Key` | full gate (ARC-004 §3) + resource state (ARC-006 §2) | JSON descriptor incl. snippet text; grants; ticket URLs | none |
| H4 delivery | `usercontent.zaa.gl/d/<ticket>` | no (ticket only) | ticket MAC, grant row, resource state | raw text (attachment), never HTML | never |

Invariants (tested, addendum §3.3, §4.2): H1 never serves content bytes, never sets a cookie, never reads the key or
password; H3 re-runs the full gate on every call and never trusts that the visitor came through H1; only H4 serves raw
bytes, and only under the addendum §4.4 headers.

## 2. H1: `GET|HEAD zaa.gl/<code>` (built)

Evidence: route regex `src/Zaagl.Api/Endpoints/ResolutionEndpoints.cs:24` (one segment, 4-64 characters, leading
alphanumeric: 1-3 character paths and multi-segment paths such as `/ab`, `/p/x`, `/f/x` match no route and get `404`
before any I/O, addendum §3.1/§3.4); content branch `src/Modules/Resolution/Application/ResolutionService.cs:77-78`
and `:114-134`; response mapping `ResolutionEndpoints.cs:56-72`.

Decision table for `target_kind = snippet` (also file, media; only the viewer segment differs):

| Link state (gate, `LinkStateCalculator`) | Answer | Body | Analytics |
|---|---|---|---|
| unknown code | `404` static page | `StatusPages.NotFound` | miss counted for the miss limiter (`:52`) |
| `Purgeable` (incl. **guest** content after `expires_at`, because guest `purge_after = expires_at`, addendum §9.3) | `404` | not-found page | miss |
| `Deleted` (owner delete, PD-55) | `410` | expired/deleted page (`:65`) | miss |
| `Disabled` admin/user | `410` | disabled page | miss |
| `Disabled` abuse/safety | `410` | acceptable-use page with report link | miss |
| `Quarantined` (account content after `expires_at`) | `410` | expired page; **never a fallback URL** (content links have none, ARC-002 §5.5, `ResolutionService.cs:122`) | miss |
| `Active` or password-protected (`Locked`) | `302 Location: {Frontend:BaseUrl}/p/<code>` | none | GET: one `Redirect` (kind 0) event for account content, guest counter only for guest content (`:132`); HEAD: nothing |

Fixed details:
- `Location` is built from `Frontend:BaseUrl`, never the request `Host`; the code is `Uri.EscapeDataString`-encoded;
  no fragment is added. The browser re-applies the visitor's `#k=` to the redirect target (RFC 9110 §10.2.2), which is
  why the key survives the hop without H1 ever seeing it.
- Headers: `Cache-Control: private, max-age=0`, `X-Robots-Tag: noindex` (`ResolutionEndpoints.cs:37-38`), no
  `Set-Cookie`. Static pages use `no-store` (`StatusPages`).
- **HEAD** answers exactly like GET (same status and `Location`) and records nothing. PD-47 does not apply: content
  links never have `MaxClicks` (addendum §3.7, ARC-002 §5.5).
- **Bots and link-preview crawlers** get the same `302` as people (PD-48 is about click-limited links only). H1 never
  produces an Open Graph page for content (`HasOpenGraph` is false for content links, ARC-002 §6 row 19); a crawler that
  follows the redirect gets H2's meta tags (§5). Bot classification for the kind-0 event is the Phase 0 one.
- Resource state (processing, rejected, burned) is **not** checked here: H1 reads only the cached link snapshot. A
  burned or rejected resource still redirects; the viewer then shows the state from H3. This keeps H1 at Phase 0 cost.

Acceptance (ARC-003 in ARCHITECTURE.md §6): `zaa.gl` never sets `Set-Cookie`, never serves content bytes, returns
`404` for `/ab` and `/f/x`. All three hold by construction above; the tests are QA work deferred by PD-51.

## 3. H2: the viewer page `app.zaa.gl/p/[slug]`

1. SSR renders a shell only (title "Zaagl", the page frame, no content) with the per-request nonce CSP of ADR-0023
   (PD-53), `Referrer-Policy: no-referrer`, `X-Robots-Tag: noindex, nofollow` and `Cache-Control: private, no-store`.
   SSR additionally produces crawler meta tags (§5).
2. Client script reads `location.hash`, accepts only `#k=<22 base64url chars>` (anything else is ignored, not sent),
   keeps the key in memory, and immediately calls `history.replaceState(null, "", location.pathname)` so the key leaves
   the address bar and the history entry. The key is never written to `localStorage`, `sessionStorage`, cookies, logs,
   analytics or error reports.
3. Client calls `GET /bff/api/public/content/{code}` with `X-Zaagl-Link-Key` (if a key was present) and `X-Zaagl-Grant`
   (if an unlock grant is held in memory). The BFF forwards both headers (its allow-list includes them, ARC-004 §2) and
   the client IP (`X-Forwarded-For`, BFF listed in `Network:KnownProxies`, BE-030), adds nothing else, and never caches
   the response.
4. The page renders from the descriptor's `state` / problem `code` (§4.2). Snippet text is rendered as data only
   (ARC-010 §6). "Copy raw" copies `snippet.content` locally (no request). "Download raw" opens `rawUrl` (H4,
   attachment).
5. The key is kept for the life of the page so that unlock, version switching (owner) and ticket refresh can re-send it.
   Reloading the page after `replaceState` loses the key: the viewer shows "This link needs its full URL including the
   part after #" (a 404 from H3 looks the same, by design).

## 4. H3: content API for a snippet

### 4.1 `GET /api/public/content/{code}` (descriptor)

Order (addendum §5.2, ARC-004 §3, ARC-006 §2), first failing step answers:

1. Snapshot by `SlugRules.Key(code)`; unknown → `404 not_found` (dummy hash compare, ARC-004 §2 equal timing).
2. Link state: `Purgeable` → `404`; `Deleted` → `410 deleted`; `Disabled` → `410 disabled` or `410 taken-down`;
   `Quarantined` → `410 expired`.
3. Purpose `View` on a URL link → `404`.
4. Link key (keyed links): missing or wrong → `404 not_found`, identical body and headers to step 1.
5. Password: no valid grant for this link and purpose `View` with the current password stamp → `401 password-required`
   (no `WWW-Authenticate`). The viewer shows the password form.
6. Resource state (`ResourceStateCalculator`): `Burned` → `410 burned` (not reachable for snippets in Phase 1);
   `Processing` → `200` descriptor with `state: processing` and no `snippet` block (a snippet is `Pending` only for
   the milliseconds between link issue and `MarkReady`, ARC-010 §4.1; the viewer retries after 1 s, at most 5 times);
   `Rejected` → `422 content-rejected`; `Ready` → step 7.
7. `200` descriptor: `kind: snippet`, `state: ready`, `title`, `expiresAt`, `hasPassword`, `burnAfterDownload: false`,
   `sizeBytes`, `snippet` = `SnippetViewDto` of the **current version only** (`versions: [current]`, see §8 Q2),
   `rawUrl` = a `Raw`-purpose ticket for that version (ARC-010 §7), `inlineUrl`/`previewUrl` null.
   Headers: `Cache-Control: private, no-store`, `X-Robots-Tag: noindex`.

The descriptor never consumes anything and writes no analytics event until PD-10 is approved (addendum §12.4).
Rate limit: content-read policy per IP partition and code, **OPEN (ARC-013)**; until defined, the existing `unlock`
partition shape (IP + code) with a higher budget is the technical default (`RateLimits:ContentReadPerMinute`, 120).

### 4.2 Viewer outcomes (one table for FE and CLI)

| H3 answer | Viewer page | CLI `zaagl get` exit |
|---|---|---|
| `200 state=ready` | render | 0, prints content |
| `200 state=processing` | spinner, retry | retries, then 75 (`EX_TEMPFAIL`) |
| `401 password-required` | password form → unlock (§4.3) | prompts for password if a TTY, else 77 |
| `404 not_found` | "Not found or the link is incomplete" (same text for a missing key) | 66 |
| `410 expired` | "This content has expired" | 69 |
| `410 deleted` | "This content was deleted" | 69 |
| `410 disabled` | "This content is not available" | 69 |
| `410 taken-down` | acceptable-use page with report link (addendum §17) | 69 |
| `422 content-rejected` | "This content is not available" | 69 |
| `429` | "Too many requests, try again shortly" (`Retry-After`) | 75 |

Exit codes are the BSD `sysexits` values; the CLI owner may change them, the mapping of states is fixed here.

### 4.3 Unlock (password, Starter only per PD-54)

`POST /api/public/content/{code}/unlock {password}` with the key header, exactly ARC-004 §4.1. Returns
`{grant, expiresAt}` (10 minutes, purposes View, Raw, Preview, Download). The viewer keeps the grant in memory and
re-sends it as `X-Zaagl-Grant`; after expiry the next H3 call answers `401` and the viewer asks for the password again.

### 4.4 Raw (H4)

`rawUrl` from the descriptor is `https://usercontent.zaa.gl/d/<ticket>` (purpose `Raw`, derivative `v<n>`, lifetime
`Content:InlineTicketMinutes` = 15). For snippets the delivery hop returns the text itself instead of an
`X-Accel-Redirect` (the text lives in PostgreSQL, ARC-010 §7). A stale `rawUrl` gets `403 ticket-invalid`; the viewer
refreshes the descriptor and retries once. HEAD answers the same headers without a body and records nothing.

## 5. Crawler previews (Open Graph) for snippets

Rule (addendum §3.6): previews only for **public** (keyless), **ready** content without a password. H1 never renders
them; H2's SSR does:

1. SSR calls `GET /api/public/content/{code}` server-side **without** a key and without a grant (the server never has
   the fragment). This call consumes nothing and is not an analytics event.
2. `200 state=ready` (therefore public and without a password): meta `og:title` = the title, or "Snippet" + language
   display name when there is no title; `og:description` = "{language} · {lineCount} lines · zaa.gl"; `og:type` =
   `article`; **no snippet text** in any meta tag and **no `og:image`** for snippets in Phase 1 (only media get previews,
   addendum §3.6). All values HTML-escaped by React.
3. Any other answer (404 for keyed links, 401, 410, 422, processing): generic meta ("Zaagl", no title, no description
   that depends on the content). A keyed snippet therefore looks exactly like an unknown code to a crawler.
4. `X-Robots-Tag: noindex, nofollow` on every viewer response, public or not (see §8 Q3).

## 6. Sequences

Redirect and view (keyed snippet, no password):

```
Browser            zaa.gl (H1)           app.zaa.gl (H2 + BFF)               api.zaa.gl (H3)
  | GET /Ab3xY#k=K (fragment stays local)
  |------------------>| snapshot (cache) -> Active
  |<------------------| 302 Location: https://app.zaa.gl/p/Ab3xY   (no cookie)
  | GET /p/Ab3xY#k=K  (browser re-applies #k=K)
  |-------------------------------------->| SSR shell + nonce CSP; SSR meta call (no key) -> 404 -> generic meta
  |<--------------------------------------|
  | script: read K, replaceState("/p/Ab3xY")
  | GET /bff/api/public/content/Ab3xY  X-Zaagl-Link-Key: K
  |-------------------------------------->| forward (session cookie not needed)
  |                                        |------------------------------------->| gate: state, key hash ok
  |                                        |                                      | resource Ready -> descriptor + rawUrl
  |<--------------------------------------|<-------------------------------------| 200 private, no-store
  | render text as data (ARC-010 §6)
```

Unlock (password snippet, Starter owner):

```
  | GET /bff/api/public/content/C  +key           -> 401 password-required
  | POST /bff/api/public/content/C/unlock {pw} +key -> gate steps 1-5 (View), BCrypt ok -> 200 {grant, exp}
  | GET /bff/api/public/content/C  +key +grant    -> 200 descriptor
```

Raw download:

```
  | click "Download raw" -> GET https://usercontent.zaa.gl/d/<ticket(Raw, v3)>
  | Nginx /d/ -> api:8081 /internal/delivery/<ticket>: MAC, grant row, resource Ready
  |   <- 200 text/plain; charset=utf-8, Content-Disposition: attachment; filename="Ab3xY.txt" (body from PostgreSQL)
  | Nginx adds nosniff, CSP sandbox, no-store, no-referrer, CORP, X-Robots-Tag (ARC-008 §10)
```

Closed link (expired account snippet):

```
  | GET zaa.gl/Ab3xY -> 410 static expired page (no redirect, no fallback)
  | (a visitor who opens app.zaa.gl/p/Ab3xY directly) -> H3 410 expired -> "This content has expired"
```

## 7. Implementation checklist

- [ ] BE: descriptor endpoint composes `ResourceDescriptor` + `ISnippetReader.GetViewAsync` and sets
      `versions = [current]` on the public route (owner routes return all versions).
- [ ] BE: wrong key and unknown code give byte-identical responses (status, body, headers).
- [ ] BE: content-read rate limit partitions by IP **and** `code` (the route parameter is `code`, ARC-004 §4.1 note).
- [ ] BE: no analytics write on H3/H4 until PD-10.
- [ ] FE: key handling of §3.2 (accept only 22-char base64url; `replaceState` before the first `await`).
- [ ] FE: SSR meta of §5; never put snippet text in meta tags.
- [ ] FE: outcome table §4.2 in en and ar.
- [ ] CLI: `zaagl get <url>` parses `<code>` and `#k=`, sends the key only in `X-Zaagl-Link-Key`, never logs the URL
      with its fragment.

## 8. Questions for the user

- Q1 (ARC-002 Q1, still OPEN) A retried snippet create returns the link without its key today; the CLI and desktop app
  retry on bad networks. Which option: new key on retry, keep the key encrypted for 24 hours, or keep as built?
- Q2 Older versions and diffs for visitors: may people who open a shared snippet see its older versions and the diff,
  or only the latest version (owner sees all)? Interim: latest only.
- Q3 Search engines: should public (keyless) snippets be indexable, or stay `noindex` like private ones? Interim:
  `noindex` everywhere.
