# E2.1 Webhook and payload catcher (`zaa.gl/hook/<token>`)

PRD: "Generate a temporary link to catch and inspect incoming API webhooks in real time during software testing."

## Shape
- A **catcher** belongs to an account. It has a public **ingest URL** `https://zaa.gl/hook/<token>` and an owner-only
  inspector API on `api.zaa.gl`.
- `<token>` is 128 bits from a CSPRNG, base64url (22 characters), case-sensitive. It is **not** a short code: codes are ~30
  bits and "never an access token" (spec §1, §5.3), and an enumerable ingest URL would let anyone spray other people's
  catchers. `hook/` is already a reserved first segment and multi-segment paths are never codes (spec §4.5 rule 6), so the
  route cannot collide with a link. The token is a **write-only** capability: knowing it lets you add requests, never read them.
- Ingest is served on the short host only (host routing: `/hook/...` is a redirect-shaped path, so `api.zaa.gl/hook/...`
  answers `421`). `zaa.gl` keeps its rules: no cookie, nothing rendered.

## Data model (`devtools` schema)
`devtools.hooks`: `id` uuid PK, `owner_id`, `token` (varchar(22) collate "C", unique), `label` (≤ 100), `response_status`
(200-299, 400-599), `created_at`, `expires_at`, `purge_after` (= `expires_at + Hooks:ReadOnlyGraceHours`, default 24 h),
plan snapshot at creation (`max_requests`, `max_body_bytes`, `max_stored_bytes`, `ingest_per_minute`), the per-minute
window (`window_start`, `window_count`), counters (`received_total`, `stored_count`, `stored_bytes`, `last_received_at`).

`devtools.hook_requests`: `id` bigint identity, `hook_id` (FK, cascade), `received_at`, `method` (≤ 16), `path` (the part
after the token, ≤ 2,048), `query` (raw, ≤ 8,192), `headers` jsonb (array of `[name, value]`, hop-by-hop and
address-revealing proxy headers removed, ≤ 100 headers, values ≤ 8 KB), `content_type` (display only), `body` bytea (up to
the plan cap), `body_bytes_total`, `body_truncated`, `source_fingerprint` (see privacy), `stored_bytes`.

The plan snapshot means a downgrade never changes a live catcher; ingest never looks up the plan (fast path).

## Endpoints
Code: `src/Zaagl.Api/Endpoints/WebhookEndpoints.cs`, `src/Modules/DeveloperTools/Application/HookService.cs`,
`HookIngest.cs`. Tests: `tests/Zaagl.Api.Tests/Phase2/WebhookCatcherTests.cs`.

| Method and route | Who | What |
|---|---|---|
| `ANY zaa.gl/hook/{token}[/{**path}]` | anyone holding the URL | Stores the request, answers the configured status with an empty `text/plain` body. |
| `POST /api/hooks` | JWT or API key `tools:write` | Create `{label?, responseStatus? (default 200), lifetimeHours?}` → `201 HookDto` (with `ingestUrl`). |
| `GET /api/hooks` | owner (`resources:read`) | The caller's catchers (live and in grace). |
| `GET /api/hooks/{id}` | owner | One catcher with counters. |
| `PATCH /api/hooks/{id}` | owner (`tools:write`) | `{label?, responseStatus?}`. |
| `DELETE /api/hooks/{id}` | owner (`tools:write`) | Deletes the catcher and every captured request now. |
| `GET /api/hooks/{id}/requests?before=&limit=` | owner | Newest first, metadata only (no body), cursor = request id. |
| `GET /api/hooks/{id}/requests/{requestId}` | owner | Full request: headers, `bodyBase64`, and `bodyText` when the body is valid UTF-8 text. |
| `DELETE /api/hooks/{id}/requests/{requestId}` | owner (`tools:write`) | Delete one. |
| `DELETE /api/hooks/{id}/requests` | owner (`tools:write`) | Clear all. |
| `GET /api/hooks/{id}/stream` | owner | Server-Sent Events: `event: request` per new capture (summary JSON, `id:` = request id, `Last-Event-ID` resumes), `: ping` every 15 s, `event: closed` when the catcher expires or is deleted. Closed by the server after 10 minutes (clients reconnect with `Last-Event-ID`). At most 3 open streams per user. |

Why SSE and not the SignalR hub: the stream is one-way, per catcher, and works through the BFF as a plain proxied response
(the BFF adds the bearer server-side, so no token reaches page script, PD-41). The analytics hub's ticket flow stays as it is.

## Ingest rules (in order, all before any write)
1. Token syntax `^[A-Za-z0-9_-]{22}$` checked before I/O; anything else `404`.
2. Unknown, deleted or expired catcher: `404` (one answer, no oracle).
3. `Content-Length` above the ingest ceiling (`ToolCeilings.HookBodyBytes`, 1 MiB) → `413` without reading; a chunked body
   that passes the ceiling is cut off with `413`. The body is streamed: at most the catcher's `max_body_bytes` are kept, the
   rest is only counted (`body_truncated = true`).
4. Per-IP limiter (`hook-ingest` policy, `RateLimits:HookIngestPerMinute`, default 600/min per IP partition) and the
   per-catcher window (`ingest_per_minute`, a conditional update under the row lock, safe across instances) → `429` with
   `Retry-After`.
5. One transaction holding the catcher row lock: insert, then drop the **oldest** captures until both `max_requests` and
   `max_stored_bytes` hold, then refresh the counters. Storage per catcher is therefore hard-bounded.

Response: the configured status, empty body, `Content-Type: text/plain; charset=utf-8`, `X-Content-Type-Options: nosniff`,
`Content-Security-Policy: sandbox; default-src 'none'`, `Cache-Control: no-store`, `X-Robots-Tag: noindex`. **The response
body and headers are not configurable**, and 1xx/3xx statuses are refused: a configurable body would turn `zaa.gl` into
free hosting for phishing pages, and a configurable `Location` into an open redirect.

## Security and abuse analysis
- **No SSRF:** the catcher never makes an outbound request (no forwarding or replay to a URL). "Replay-safe": captures are
  inert records; replaying them to a target is a client-side feature if ever wanted (open question).
- **Never executed or rendered:** bodies are bytes in the database, returned only as JSON (`bodyBase64`, optional `bodyText`)
  to the owner. Nothing is served as a document from any host. The UI must render `bodyText` as escaped text.
- **NUL and control characters:** header values, path and query are stored as text with NUL replaced by U+FFFD (PostgreSQL
  text/jsonb cannot hold NUL); the body is bytea and kept exactly.
- **Privacy:** no IP address is stored. `source_fingerprint` = first 8 bytes (hex) of HMAC-SHA256(`GUEST_KEY`-derived key,
  IP partition), so the owner can tell "same sender" from "different sender" without an address. `X-Forwarded-For`,
  `X-Real-IP`, `Forwarded`, `CF-Connecting-IP`, `True-Client-IP` are removed before storage.
- **Abuse as a dead drop:** accounts only, short lifetimes, strict caps, takedown by deleting the catcher (admin action is an
  open question). The token appears in the reverse proxy and request logs as a path (write-only capability; accepted).
- **Races:** live-catcher cap per owner under a transaction-scoped advisory lock (`pg_advisory_xact_lock`), the per-minute
  window and storage trimming under the catcher row lock (tested with parallel requests).
- **Case:** the token is case-sensitive everywhere (lookup collation "C", rate-limit key = exact token), so case variants
  are different (unknown) catchers rather than extra rate-limit buckets for one catcher.

## Retention
At `expires_at` ingest stops (`404`); the owner can still read for `Hooks:ReadOnlyGraceHours` (24 h); at `purge_after` the
worker's DeveloperTools sweep deletes the catcher and its captures. Owner delete is immediate.

## Plan gating (PROPOSED)
Guests: none (a guest could never read the inspector: no management for guests, spec §7.12).
Free: 1 live catcher, 24 h, 100 requests, 64 KB stored per body, 5 MB total, 30 requests/min.
Starter: 10 live catchers, 7 days, 1,000 requests, 512 KB per body, 50 MB total, 120 requests/min.

## What the UI needs
1. **Catcher list** (`/hooks`): label, ingest URL with copy button, expires in, received / stored counts, status chip
   (live, read-only, expired); "New catcher" (label, response status picker 200/201/202/204/400/401/403/404/409/422/429/500/503,
   lifetime); 409 `limit-reached` and 402 states.
2. **Inspector** (`/hooks/<id>`): left list of requests (method badge, path, time, size, truncated marker) fed by the SSE
   stream with a "live" indicator and reconnect; right detail: method, path, query (parsed table), headers table, body as
   text (escaped, JSON pretty-printed client-side) or hex view for binary, copy-as-curl (client-side), sender fingerprint;
   delete one / clear all; empty state with sample `curl` command.
3. Expired/read-only banner; deleted catcher → 404 page.

## Open questions
1. Plan values above (PRD: Pro tier).
2. Should the inspector show the sender's real IP (webhook.site does)? Proposed: no, fingerprint only (privacy rules).
3. Configurable response body or headers (proposed: never, see above). Custom response delay?
4. Forward/replay captured requests to a URL (would need full SSRF protection; proposed: not in this slice).
5. Admin takedown of a catcher (route under `/api/admin/*`), and should catchers show in the account data export?
