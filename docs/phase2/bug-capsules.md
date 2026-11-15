# E2.1 Bug capsules

PRD: "Record a quick screen clip that automatically bundles browser console errors and network logs for engineers."

## Model
A capsule is a **content link of kind `capsule` (5)** (viewer `app.zaa.gl/b/<code>`) whose row in `devtools.capsules` holds:
- an optional **recording**: an existing upload of the same owner, made through the normal Phase 1 tus path (scanned, stored,
  served by ticket), never copied. It must be a validated **video** (MP4 or WebM, detected from the bytes, viewer `video`);
- the **log document** (schema below), validated, redacted and stored as `jsonb`, with counts for the card.

`devtools.capsules`: `resource_id` PK, `owner_id`, `recording_resource_id` (null = logs only), `logs` jsonb, `logs_bytes`
(≤ 4 MiB, checked), `console_count`, `error_count`, `network_count`, `failed_request_count`, `created_at`.

### Video recordings (Files)
The viewer-type mechanism of feature 6 gains **video**: MP4 (`ftyp` with an ISO/MP4 brand: `isom`, `iso2`, `iso4`-`iso6`, `mp41`,
`mp42`, `avc1`, `dash`, `M4V `, `MSNV`) and WebM (EBML header with DocType `webm`; Matroska and QuickTime are not accepted) are
detected from the first bytes, get `storage_objects.viewer = 4` (migrations `WidenViewerKindForVideo` +
`ValidateViewerKindForVideo`, expand-only) and are served inline by an Inline ticket with `Range` support (addendum §4.4 allows
video inline; nosniff and the sandbox CSP still apply). The media allowlist (PD-16) is unchanged: a video upload is a `file`
resource with a video viewer. A side effect worth knowing: **every** MP4/WebM file upload now gets an inline player in the file
viewer (previously download-only).

## Log schema (version 1)
```json
{
  "v": 1,
  "console": [ { "t": 120, "level": "error|warn|info|log|debug", "message": "...", "source": "app.js:10:5", "stack": "..." } ],
  "network": [ { "t": 200, "method": "POST", "url": "https://...", "status": 500, "durationMs": 87.5, "type": "fetch",
                 "requestHeaders": { "Content-Type": "application/json" }, "responseHeaders": { },
                 "requestBodySize": 120, "responseBodySize": 0, "error": "net::ERR_FAILED" } ],
  "environment": { "userAgent": "...", "platform": "...", "language": "en-GB", "url": "https://...", "timestamp": "ISO 8601",
                   "viewport": { "w": 1280, "h": 720 } }
}
```
- `t` is milliseconds from the start of the recording (or of the capture when there is no recording); the viewer syncs the log
  timeline with the video by it. `t`, `durationMs` and the body sizes must be finite JSON numbers, 0 or more (the error message
  says exactly that; it no longer prints the internal bound `1.7976931348623157E+308`).
- Strict: unknown fields, wrong types, NUL characters, unknown levels, status outside 0-999 are `400`. Limits: 5,000 console and
  5,000 network entries, `message` 8 KB, `stack` 16 KB, `source` 2 KB, `url` 4 KB, 50 headers per map, header values 4 KB.
- **No bodies** in the schema (request/response bodies are where secrets live); only their sizes.
- Plan cap on the raw document: Free 512 KB, Starter 2 MB (PROPOSED), `413 logs-too-large`.
- **Server-side redaction** (defence in depth; the client SHOULD redact first): values of `Authorization`, `Proxy-Authorization`,
  `Cookie`, `Set-Cookie`, `X-Api-Key`, `X-Auth-Token`, `X-CSRF-Token`, `X-XSRF-Token`, `X-Zaagl-Link-Key`, `X-Zaagl-Grant`,
  `Captcha-Token` → `[redacted]`; URL query values of `token`, `access_token`, `refresh_token`, `id_token`, `key`, `api_key`,
  `apikey`, `secret`, `client_secret`, `password`, `pass`, `pwd`, `auth`, `code`, `session`, `sessionid`, `sig`, `signature`,
  `credential`, `api-key`, `x-api-key`, `access-token`, `auth_token`, `authtoken`, `jwt`, `bearer`, `private_key`, `passwd`,
  `X-Amz-Signature`, `X-Amz-Credential`, `X-Amz-Security-Token` → `[redacted]`; URL fragments → `#[redacted]`; `user:password@`
  before the host → `[redacted]@`. Query parameters are split on both `&` and `;`; each key is percent-decoded (repeatedly, `+`
  as a space), trimmed and matched case-insensitively by its base name **and** every bracket segment, so `token[]=`,
  `api_key[0]=`, `auth[token]=`, `user[password]=`, `%74oken=` and `a=1;token=…` are all redacted (`CapsuleLogs.RedactUrl`,
  tests `CapsuleRedactionTests`). The stored document is rebuilt from known fields only.

### Client capture guidance (for the recorder UI / extension)
Capture `console.*` and `window.onerror`/`unhandledrejection` (message, source, stack), `fetch`/XHR metadata (method, URL,
status, timing, selected headers, sizes) and the environment. Never capture bodies, cookies, `localStorage`, form values or
password fields. Redact before upload with the same lists as above. Record the screen with `getDisplayMedia` +
`MediaRecorder` (WebM in Chromium/Firefox, MP4 in Safari), upload it with tus as `kind=media` (it becomes a `file` with a video
viewer), wait for `ready`, then create the capsule.

## Endpoints
Code: `src/Zaagl.Api/Endpoints/DeveloperToolsEndpoints.cs` (`MapCapsules`), `src/Modules/DeveloperTools/Application/CapsuleService.cs`,
`CapsuleLogs.cs`; video detection in `src/Modules/Files/Application/MediaSniffer.cs`. Tests:
`tests/Zaagl.Api.Tests/Phase2/BugCapsuleTests.cs`, `tests/Zaagl.Files.Tests/ViewerTypeTests.cs`.

| Method and route | Who | What |
|---|---|---|
| `POST /api/capsules` | JWT or API key `tools:write` | `{logs, recordingResourceId?, title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/capsules/{id}` | owner (`resources:read`) | Owner view: state, summary, recording state and inline ticket. |
| `DELETE /api/capsules/{id}` | owner (`tools:write`) | Link deleted (410); the recording upload is untouched. |
| `GET /api/public/capsules/{code}` | link holder | `{kind:"capsule", title, expiresAt, hasPassword, recording:{state, mime, sizeBytes, inlineUrl}|null, summary:{consoleCount, errorCount, networkCount, failedRequestCount, logsBytes}, logs}` |
The owner's list is `GET /api/resources?kind=capsule`; renew, password, public via `PATCH /api/resources/{id}`.

## Security notes
- Logs are returned as JSON data; the UI renders every string escaped (a console message `<script>` is text).
- The recording is the owner's own scanned upload; its own link state is re-checked on every read and by the ticket hop.
- Expiry is capped by the recording's expiry. Keyed by default (`#k=`); `public` allowed like other content.
- Accounts only (guests have no owned recording and cannot manage content).

## Retention
Content retention (7 / 30 days). Purge removes the row through `CapsuleKindHandler`; the recording follows its own lifecycle.
The log bytes count toward the owner's storage quota (reserved at creation).

## Plan gating (PROPOSED)
Guests no; Free yes (512 KB logs); Starter yes (2 MB logs).

## What the UI needs
1. **Recorder** (web page, later a browser extension or the desktop app): start/stop screen capture, live console/network
   capture, a review step that shows exactly what will be uploaded with a redaction preview and the ability to drop entries,
   upload progress (tus), "processing" while the scan runs, then the share URL.
2. **Viewer `app.zaa.gl/b/<code>`**: video player (inline ticket, seek) on one side, log panes with tabs (Console, Network,
   Environment), filters (errors only, failed requests), a timeline synced to the video (`t`), copy as text, "logs only" layout
   when there is no recording, `recording.state = unavailable` notice, password form, 404/410 states.
3. Owner card: counts (errors, failed requests), recording thumbnail state, delete, renew.

## Open questions
1. Allow guests to create capsules (would need guest recordings and no owner management)?
2. Accept HAR files directly as the network log (convert server-side)?
3. Should the recording be re-encoded or trimmed (addendum §6.7 forbids touching originals; trimming would be a derivative)?
