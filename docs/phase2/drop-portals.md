# E2.3 "Reverse Pigeon" drop-off portals

PRD: "Create a secure upload link for an external client to send large files back to you without needing an account." (`zaa.gl/drop/…`)

## Model
A portal is a **content link of kind `drop` (8)**. Its row in `collab.drop_portals` holds the owner's settings; the files people send
become **ordinary File/Media resources of the portal owner** (owner library, owner quota, scanned, served by ticket), attributed in
`collab.drop_files`. Nothing new is stored in Files except two columns on the upload session.

- `collab.drop_portals`: `resource_id` PK, `owner_id`, `instructions` (≤ 2,000, plain text), `max_files` (1-10,000), `max_bytes`,
  `max_file_bytes`, `allowed_types` (comma list of type families, empty = any), `require_name`, `require_email`, `notify_owner`,
  `closed_at`, `last_notified_at`, `created_at`.
- `collab.drop_files`: `resource_id` PK (the received file), `portal_id`, `owner_id`, `upload_session_id` (unique), `uploader_name`
  (≤ 100), `uploader_email` (≤ 254), `note` (≤ 1,000), `received_at`. No IP, no user agent.
- `files.upload_sessions` gains `drop_portal_id` and `drop_token_hash` (SHA-256 of the uploader's per-session token), check
  `ck_upload_sessions_drop` (both or neither, and a drop session always has the owner) and index `ix_upload_sessions_drop_portal`.

### URLs
`POST /api/drops` answers `shortUrl = zaa.gl/drop/<code>` (plus `#k=<linkKey>` to append when the portal is keyed, the default). The short
host serves `GET|HEAD zaa.gl/drop/<code>` → `302 app.zaa.gl/drop/<code>` (only for a drop code; any other code or kind is the branded
404); `zaa.gl/<code>` also works (Phase 1 redirect, viewer segment `drop`). `drop` was already a reserved first segment
(`SlugRules.Reserved`), so no alias can shadow it.

### Type families (`UploadTypeCategories`, decided from the bytes at `/complete`)
`image` (PNG/JPEG/GIF/WebP), `video` (MP4/WebM), `audio`, `pdf`, `archive` (ZIP, GZip, 7z; **Office files are ZIP containers and fall
here**), `model` (glTF), `other` (anything not recognised). Executables and encrypted archives are refused for every drop upload,
whatever the owner's plan says; a refused file is aborted at once (bytes deleted, reservation and portal slot released).

## Upload flow (uploader, no account)
1. `GET /api/public/drops/{code}` (link key header / password grant as for any content) → title, instructions, `state`
   (`open|closed|full`), effective `maxFileBytes`, `allowedTypes`, `requireName`, `requireEmail`. **Nothing about other uploads.**
2. `POST /api/public/drops/{code}/uploads` = tus creation (`Upload-Length`, optional `Upload-Metadata: filename …`). Gates, in order:
   content gate → portal open → guest panel feature `guest-drop` (admin pause, **CAPTCHA per upload session**, action `guest-drop`) →
   in-memory guest brakes (per minute, global ceiling) → per-network counters `Drops:PerIpPerHour` (30) / `Drops:PerIpPerDay` (100)
   (separate from the PD-14 content counters, refunded if the create fails) → owner plan file size and the portal's per-file cap →
   disk admission → **portal caps under a per-portal advisory lock** (files, total bytes, uploads per hour; held until the session row is
   committed) → **owner quota reservation** (`IContentQuota.ReserveAsync`, atomic). Answer `201` with `Location`,
   `X-Zaagl-Drop-Token` and the same values in the JSON body (`uploadId`, `uploadUrl`, `token`, `expiresAt`).
3. `HEAD|PATCH|DELETE /api/public/drops/{code}/uploads/{id}` with `X-Zaagl-Drop-Token` (plus the link key / grant): the Phase 1 tus
   machinery (per-session advisory lock, offsets, 90 MB chunks, 24 h idle expiry). A missing or wrong token is `404`; the owner's own
   `/api/uploads/{id}` routes never see a drop session (and a drop token never reaches an owner session).
4. `POST …/{id}/complete` (`Idempotency-Key` required) with `{name?, email?, note?}`: required fields are checked **before** the
   upload is finished; then the Phase 1 `/complete` (sniff, type policy, register adopting the reservation, keyed link whose key is
   discarded, scan task). Answer `202 {uploadId, status: "received", fileName, sizeBytes}`: never a code, link or resource id.

The file is **not downloadable until the scan is clean** (Phase 1 ticket rules); the owner sees `scan: pending|clean|infected|error`.
Closing a portal stops new uploads; uploads already admitted may finish (like a guest-upload pause).

## Endpoints
Code: `src/Zaagl.Api/Endpoints/DropEndpoints.cs`, `src/Modules/Collaboration/Application/DropPortalService.cs`, Files changes in
`UploadService.cs`. Tests: `tests/Zaagl.Api.Tests/Phase2/DropPortalTests.cs`, `GuestControlsTests.Drop_portal_uploads_follow_the_guest_drop_switches`.

| Method and route | Who | What |
|---|---|---|
| `POST /api/drops` | JWT or API key `tools:write` | `{title?, instructions?, maxFiles?, maxBytes?, maxFileBytes?, allowedTypes?, requireName, requireEmail, notifyOwner (default true), expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/drops` | owner (`resources:read`) | Owner's portals (≤ 100) with `filesReceived`, `bytesReceived`, `accepting`. |
| `GET /api/drops/{id}` | owner | One portal. |
| `PATCH /api/drops/{id}` | owner (`tools:write`) | Settings (instructions, caps, types, require*, notifyOwner). Title/expiry/password/public: `PATCH /api/resources/{id}`. |
| `POST /api/drops/{id}/close`, `/reopen` | owner (`tools:write`) | Stop / resume new uploads (reopen counts toward the open-portal cap). |
| `DELETE /api/drops/{id}` | owner (`tools:write`) | Portal link deleted (uploaders get 410); received files stay in the library. |
| `GET /api/drops/{id}/files` | owner (`resources:read`) | Received files (≤ 500, newest first): name, size, type, scan, state, uploader name/email/note. Download: `POST /api/resources/{fileId}/download`. |
| `GET /api/public/drops/{code}` | uploader (gate) | Descriptor above. |
| `POST /api/public/drops/{code}/uploads` | uploader (gate + CAPTCHA) | tus create. |
| `HEAD/PATCH/DELETE /api/public/drops/{code}/uploads/{id}` | uploader (gate + token) | tus. |
| `POST /api/public/drops/{code}/uploads/{id}/complete` | uploader (gate + token) | Finish; receipt only. |
| `GET/HEAD zaa.gl/drop/{code}` | anyone (short host) | 302 to the uploader page. |

Errors: `409 drop-closed` (closed, full, no room, owner lost the plan feature), `409 limit-reached` (open-portal cap), `413 file-too-large`,
`403 quota-exceeded` (owner's storage), `422 content-rejected` (type family or executable), `429 quota` (per-network counter),
`429 too-many-requests` (portal hourly cap or brakes), `503 guest_creation_paused` (admin paused `guest-drop`), `400 captcha-*`.

## Plan gating (PROPOSED, `Plans:<Plan>:Tools:*`)
| | Guest | Free | Starter |
|---|---|---|---|
| Create portals (`DropEnabled`) | no | yes | yes |
| Open portals at once (`DropMaxActive`) | - | 2 | 20 |
| Files per portal, max (`DropMaxFiles`) | - | 20 | 500 |
| Bytes per portal, max (`DropMaxBytes`) | - | 250 MB | 5 GB |
| Uploads per portal per hour (`DropUploadsPerHour`) | - | 60 | 300 |
| Per file | - | plan `MaxFileBytes` (25 MB) | plan `MaxFileBytes` (100 MB) |
| Password on the portal | - | no | yes (PD-54) |
The owner's storage quota (250 MB / 5 GB) always applies on top. Per-network counters are configuration (`Drops:*`), not plan values.

## Security and abuse
- Every concurrent cap is atomic: open portals per owner (transaction advisory lock + count + insert; race test with 6 parallel creates
  and a cap of 2), files/bytes/hour per portal (Files advisory lock held until the session commit; race test with 10 parallel uploads
  and a cap of 3), owner quota (Phase 1 reservation).
- Uploaders are isolated: per-session 256-bit token, stored hashed; no list, no count, no link. The receipt has no resource id.
- Scanning: Phase 1 pipeline (clamd, fail-closed); executables refused for drop uploads regardless of plan.
- Owner routes: owner only (404 for others, admins included), `private, no-store`; uploader routes `private, no-store`, `noindex`.
- A failing credential is 401 on every route (owner and uploader).
- Notification emails carry only the owner's own title and a count: **no uploader-typed text** (no name, note or file name) ever enters
  Zaagl mail. At most one email per portal per 15 minutes (conditional UPDATE on `last_notified_at`), through the Identity outbox
  (kind `drop-received`, retry, suppression, disabled-user rules).
- Logs: no token, name, email or note is logged.

## Retention
The portal follows content retention (Free 7 days, Starter 30 days; renewable via `PATCH /api/resources/{id}`). Received files are
normal resources with the owner's retention from the moment they arrive. Purge: `DropPortalKindHandler` deletes the portal row and its
attribution rows; `DropFilePurgeHandler` (File and Media) deletes a file's attribution row when the file is reclaimed. Drop sessions
that never finish expire after 24 h idle (reservation released, slot freed).

## What the UI needs
1. **Owner: create/edit form**: title, instructions (plain text, line breaks kept), caps (files, total size, per-file size; show the
   plan maximums), allowed type checkboxes (image, video, audio, PDF, documents & archives, 3D, other), "ask for name", "ask for
   email", "email me when files arrive", expiry, password (Starter), public vs keyed. Show the URL **with `#k=`** once (copy button).
2. **Owner: portal page**: state chip (open / closed / full / expired), usage bars (files, bytes), close/reopen, delete, renew; the file
   table (name, size, type, uploader name/email, note, received, scan state; Download only when `scan = clean` and `state = ready`;
   delete via `DELETE /api/resources/{id}`). Uploader-typed text must be rendered as text.
3. **Uploader page `app.zaa.gl/drop/<code>`**: read `#k=`, call the descriptor, password form when 401 `password-required`, closed/full/
   expired states, the instructions, a drop zone with client-side type/size pre-checks, the Turnstile widget (action `guest-drop`) when
   `GET /api/guest-status` says `drop.captchaRequired` (one token per file/upload session), tus-js-client pointed at
   `/api/public/drops/{code}/uploads` with headers `X-Zaagl-Link-Key` (and `X-Zaagl-Grant`) and, after create, `X-Zaagl-Drop-Token`
   (read it from the response header or JSON), name/email/note fields, then `/complete`; a "received" confirmation per file. Show the
   paused state when `drop.enabled` is false.
4. Admin panel: the two new switches `guest-drop` and `guest-table` (enabled, CAPTCHA) from `GET/PUT /api/admin/guest-controls`
   (`drop`, `table` objects; absent on PUT = unchanged).

## Open questions
1. Approve the plan values (open portals, caps, hourly rate) and the per-network defaults (30/h, 100/day).
2. Should the uploader get an email receipt (needs their address and a template; today: none)?
3. Should received files be quarantined from the owner's normal library until the owner "accepts" them?
4. Office documents count as `archive` (ZIP). Add a `document` family that inspects the ZIP for Office/ODF markers?
5. Should a closed portal also stop resumes of uploads already admitted (today they may finish within the 24 h idle window)?
