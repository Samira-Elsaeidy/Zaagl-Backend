# E2.3 Smart PII redaction guard

PRD: "Automatically detect and blur sensitive info (emails, credit card numbers, API keys) on screenshots before sharing."

## Decision: detect in the browser, check text on the server, render on the server
| Step | Where | Why |
|---|---|---|
| OCR (find text and its boxes) | **Browser** (e.g. Tesseract.js / the platform's text detection) | No OCR engine in our images: Tesseract in the worker would add ~30-60 MB of native libraries plus language data to the Docker image, 0.5-3 s of CPU per screenshot, and a new native attack surface parsing untrusted images. The browser already has the pixels before upload. |
| PII rules (what is sensitive) | **Server**, `POST /api/redactions/detect` (text or OCR words) | One authoritative, tested rule set (Luhn, key formats) instead of rules duplicated in every client; the client sends recognised text, never the image. |
| Review | Browser | The owner sees every proposed box and can add/remove boxes before anything is rendered (detection is a guard, not a guarantee). |
| Render the redacted copy | **Server**, `POST /api/redactions` | Pixels are destroyed from the safe decode path (bomb gates, oriented decode), the result is a fresh PNG without metadata, stored and scanned through the normal pipeline, with its own link. |
| "Never stored" option | Browser | For maximum privacy the UI can paint the boxes on a canvas and upload only the result with the normal upload: the original never reaches the server. The server text check still helps the client find the boxes. |

So the server never runs OCR and never adds a dependency; **no Docker change is needed**.

## Text check (`PiiDetector`)
Input: `{text}` (≤ 64 K characters) or `{words: [{text, x, y, w, h, line}]}` (≤ 5,000 words of ≤ 200 characters, boxes normalised
0..1). Words are joined per line (space between words, newline between lines) so a card split into four OCR words is still found; every
word overlapping a detection becomes a box (word box plus a 0.2 % margin). Output: `spans` (`kind`, `start`, `length`: **positions only,
the matched text is never echoed or logged**) or `boxes` (`kind`, `word`, `x`, `y`, `w`, `h`). Every pattern has a 250 ms match timeout.

| Kind | Rule |
|---|---|
| `email` | `local@domain.tld` (local ≤ 64, labels ≤ 63, TLD 2-24 letters). |
| `card` | 13-19 digits, a single space or dash allowed between digits, **Luhn checksum valid**, not all the same digit. |
| `phone` | Optional `+CC`/`00CC`, optional `(area)`, then 2-5 groups of 2-4 digits separated by space, dot or dash; 9-15 digits in total; not a date; a card match wins. |
| `private-key` | `-----BEGIN [RSA/EC/DSA/OPENSSH/PGP/ENCRYPTED ]PRIVATE KEY[ BLOCK]-----` |
| `aws-access-key` | `AKIA`/`ASIA`/`AGPA`/`AIDA`/`AROA`/`ANPA` + 16 `[0-9A-Z]` |
| `github-token` | `ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_` + 36-255 alphanumerics, or `github_pat_` + 22-255 |
| `slack-token` | `xoxa-`/`xoxb-`/`xoxp-`/`xoxo-`/`xoxs-`/`xoxr-` + 10-200 `[A-Za-z0-9-]` |
| `stripe-key` | `sk_`/`rk_`/`pk_` + `live_`/`test_` + 16-128 alphanumerics |
| `google-api-key` | `AIza` + 35 `[0-9A-Za-z_-]` |
| `openai-key` | `sk-` (optionally `sk-proj-`, `sk-ant-`) + 20-200 `[A-Za-z0-9_-]` |
| `jwt` | `eyJ…` three base64url parts separated by dots |
| `bearer-token` | the value after `Bearer ` (16-2000 characters) |
| `secret-assignment` | the value after `api_key`, `apikey`, `secret`, `client_secret`, `access_token`, `auth_token`, `token`, `password`, `passwd`, `pwd`, `access_key` followed by `:` or `=` (6-500 characters) |
Overlaps keep the first match in this order: keys/tokens, emails, cards, phones. False positives are expected (the owner reviews).

## Render (`RedactionService`, `RedactionRenderer`)
- Source: one of the caller's own **Media** resources, ready, scanned clean, in the objects area, raster type (PNG, JPEG, GIF, WebP), not
  burn-after-download. Anything else (someone else's, a file, a missing id) is the same `422 resource-unusable`.
- Regions: 1 to plan `RedactMaxRegions` rectangles `{x, y, w, h}` normalised to the **displayed (EXIF-oriented)** image, inside 0..1, `w`,
  `h` > 0; rounded outwards to whole pixels.
- Styles: `solid` (default; opaque `fill`, default `#000000`) or `pixelate` (mosaic blocks of at least max(12 px, a quarter of the region's
  short side), each its mean colour). **No Gaussian blur**: light blur and fine mosaics of text can be partially reversed; both styles here
  destroy the glyphs.
- Decode: header pixel gate before any decoder (`Redaction:MaxPixels`, default 16,777,216 = 64 MB of RGBA), the decoder's dimensions must
  equal the header's, complete decodes only, EXIF orientation applied (`ImageThumbnailer.Orient`), at most `Redaction:MaxParallel` (2)
  renders at once per API instance (a request waits ≤ 2 s for a slot, else `503`), `Redaction:TimeoutSeconds` (20) wall clock. An
  animated image is redacted as its first frame.
- Output: a fresh PNG with **no metadata** (no EXIF/GPS/comments), as a **new Media resource** of the owner (quota reserved for the exact
  bytes), its own content link (kind media, viewer `/i/`, keyed by default, password/alias/expiry/public as for any upload), stored as a
  temp object and handed to the normal **scan task** (then thumbnail and palette). Nothing in the copy refers back to the original.
- `deleteOriginal` (default **true** = "share redacted only"): after the copy is stored, the original is deleted through the owner delete
  path (`OwnerResources.DeleteAsync`: link 410 at once, bytes removed by the Phase 1 deletion task). `false` keeps it.

## Endpoints
Code: `src/Zaagl.Api/Endpoints/RedactionEndpoints.cs`, `src/Modules/Files/Application/RedactionService.cs`, `PiiDetector.cs`.
Tests: `tests/Zaagl.Api.Tests/Phase2/PiiRedactionTests.cs` (`PiiDetectorTests` unit incl. renderer, `PiiRedactionTests` API).

| Method and route | Who | What |
|---|---|---|
| `POST /api/redactions/detect` | JWT or API key `tools:write` | `{text}` or `{words}` → `{spans, boxes}` (nothing stored). |
| `POST /api/redactions` | JWT or API key `tools:write` | `{sourceResourceId, regions[], style?, fill?, deleteOriginal? (true), title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt, status:"processing", width, height, regions, originalDeleted}` |
The copy is then an ordinary image: `GET /api/resources/{id}` (state), `GET /api/public/content/{code}` (viewer), library, renew, delete.

## Plan gating (PROPOSED, `Plans:<Plan>:Tools:*`)
| | Guest | Free | Starter |
|---|---|---|---|
| Redact (`RedactEnabled`) | no | yes | yes |
| Regions per request (`RedactMaxRegions`) | - | 50 | 200 |
| Text check | - | yes | yes |
The copy counts toward storage and follows the plan's file size and retention like an upload. Ceiling: 500 regions.

## Security and abuse
- The original is only ever read (never modified); the copy is scanned like any upload before it is served.
- Bounded CPU/memory: pixel gate, parallel slots, timeout; rate limit `content-write`.
- No PII in logs (only ids and the region count); the text check never echoes matches.
- Owner only; a failing credential is 401; API keys need `tools:write`.

## Retention
The copy follows content retention (Free 7, Starter 30 days) and the normal purge. Nothing else is stored (no detection results).

## What the UI needs
1. **Redact step** in the upload/share flow (and "Redact" on an image in the library): run OCR in the browser (Web Worker; show progress),
   send the words to `/api/redactions/detect`, draw the returned boxes over the image with their kind labels, let the owner add (drag a
   rectangle), resize and remove boxes, choose `solid`/`pixelate` (and colour), a "Delete the original" toggle (on by default) and the
   share options; then `POST /api/redactions`, poll `GET /api/resources/{id}` until `ready`, show the new link with `#k=`.
2. **Never-stored mode** (optional toggle "redact on my device"): paint the boxes on a canvas, export PNG, upload it with the normal tus
   upload; the original never leaves the browser.
3. Errors: `422` (not your image / too large to redact here: offer never-stored mode), `503` busy (retry), `402` plan.

## Open questions
1. Approve browser OCR + server text check + server render (vs. server OCR in the worker with the Docker cost above).
2. Move the render to the worker (async) if load requires; today it runs in the API with tight bounds so `deleteOriginal` can be atomic.
3. Default `deleteOriginal = true`: confirm.
4. Add detectors (IBAN with mod-97, national IDs, IP addresses)?
5. Redact video frames or PDFs (out of scope)?
