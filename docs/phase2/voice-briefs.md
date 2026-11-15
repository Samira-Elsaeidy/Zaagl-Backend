# E2.3 Voice-over micro-briefs

PRD: "Record a 30-second voice explanation while pointing at a document or image: 10x faster and lighter than recording a video call."

## Model
A brief is a **content link of kind `brief` (10)** (viewer `app.zaa.gl/vb/<code>`) whose row in `collab.briefs` references two existing
resources of the same owner, never copied, re-checked on every read:
- the **subject**: an image (PNG, JPEG, GIF, WebP: the Phase 2 `ImageParts` rules) or a **PDF** file;
- the **audio**: a voice recording uploaded through the normal tus path (scanned, stored, served by ticket). Accepted detected types:
  `video/webm` (MediaRecorder in Chromium/Firefox writes WebM; audio-only WebM is detected as `video/webm` with viewer `video`),
  `video/mp4` / `audio/mp4` (Safari), `audio/mpeg`, `audio/wav`, `audio/ogg`, `audio/aac`, `audio/flac`, with a validated viewer
  (`audio` or `video`, E2.2).

Plus the **pointer timeline** (below), stored as `jsonb`, and the declared duration.

`collab.briefs`: `resource_id` PK, `owner_id`, `subject_resource_id`, `subject_kind` (`image|document`), `audio_resource_id`
(≠ subject), `duration_ms` (1-300,000), `timeline` jsonb, `event_count` (0-20,000), `created_at`.

### Duration
The server does not decode audio (no media parser in the API: smaller attack surface, no new native dependency). The recorder declares
`durationMs` (≤ plan `BriefMaxSeconds`), and the **audio size is capped** at `BriefMaxSeconds × 48,000 B/s + 256 KB` (384 kbit/s: any
voice codec fits; Free 30 s ≈ 1.7 MB, Starter 60 s ≈ 3.1 MB) → `422 resource-unusable`. Every timeline event must fall within
`0..durationMs`. A lying client can attach a longer, low-bitrate recording within the byte cap; the cost is bounded by the byte cap
(open question 1).

## Pointer timeline (version 1, strict)
```json
{ "v": 1, "events": [
  { "t": 0,    "k": "point", "x": 0.42, "y": 0.18 },
  { "t": 1200, "k": "tap",   "x": 0.40, "y": 0.20 },
  { "t": 2100, "k": "note",  "x": 0.55, "y": 0.60, "text": "this logo" },
  { "t": 2500, "k": "page",  "p": 2 },
  { "t": 2600, "k": "point", "x": 0.30, "y": 0.70, "p": 2 },
  { "t": 4000, "k": "hide" } ] }
```
- `t`: integer milliseconds from the start of the recording, `0..durationMs`, **non-decreasing**.
- `k`: `point` (pointer position), `tap` (emphasis pulse), `note` (short label), `hide` (pointer hidden), `page` (document page change).
- `x`, `y`: normalised `0..1` from the top-left of the **displayed** (EXIF-oriented) image or of the PDF page; required for
  `point|tap|note`, forbidden for `hide|page`; rounded to 5 decimals.
- `p`: page number `1..10000`, documents only (`page` needs it; optional on the others).
- `text`: notes only, 1-140 characters, no control characters (plain text; the viewer renders it as text).
- Unknown fields, wrong types, NUL → `400`. Events ≤ plan `BriefMaxPointerEvents`; raw size ≤ events × 400 bytes → `413 input-too-large`.
- No timeline at all is valid (a pure voice note over the subject).
- Recommended client sampling: pointer moves at ≤ 20 Hz with a minimum distance of 0.005, so 30 s ≈ 600 events.

## Endpoints
Code: `src/Zaagl.Api/Endpoints/BriefEndpoints.cs`, `src/Modules/Collaboration/Application/BriefService.cs` (`BriefTimeline`).
Tests: `tests/Zaagl.Api.Tests/Phase2/VoiceBriefTests.cs` (`BriefTimelineTests` unit, `VoiceBriefTests` API).

| Method and route | Who | What |
|---|---|---|
| `POST /api/briefs` | JWT or API key `tools:write` | `{subjectResourceId, audioResourceId, durationMs, timeline?, title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/briefs/{id}` | owner (`resources:read`) | State, parts (no tickets minted), duration, event count. |
| `DELETE /api/briefs/{id}` | owner (`tools:write`) | Link deleted (410); the image/PDF and the recording are untouched. |
| `GET /api/public/briefs/{code}` | listener (gate, no account) | `{kind:"brief", state, title, expiresAt, hasPassword, durationMs, subject:{kind, state, mime, sizeBytes, width, height, inlineUrl, previewUrl}, audio:{…, inlineUrl}, timeline}` |
Owner list: `GET /api/resources?kind=brief`; renew/password/public via `PATCH /api/resources/{id}`. `state` is `incomplete` when a part
became unusable (deleted, expired, taken down); the part shows `unavailable`.

Delivery: the audio is an Inline ticket (`Range` supported, E2.2); an image subject gets an Inline ticket plus a Preview ticket on its
thumbnail; a **PDF subject gets an Inline ticket that is still served as an attachment** (PDF is not on the inline allowlist): the viewer
fetches it with `fetch()` (CORS through `X-Zaagl-Acao`) and renders it with pdf.js in a canvas. Nothing is ever a Download ticket here.

## Plan gating (PROPOSED, `Plans:<Plan>:Tools:*`)
| | Guest | Free | Starter |
|---|---|---|---|
| Create (`BriefsEnabled`) | no | yes | yes |
| Longest recording (`BriefMaxSeconds`) | - | 30 s | 60 s |
| Timeline events (`BriefMaxPointerEvents`) | - | 600 | 3,000 |
| Password | - | no | yes (PD-54) |
Ceilings: 300 s, 20,000 events. Listening needs no account on every plan.

## Security and abuse
- Parts must be the owner's own, ready, scanned clean, not burn-after-download, with their own link active; someone else's resource is
  the same `422` as a missing one (no existence oracle). Expiry is capped by both parts' expiries.
- Keyed by default (`#k=`); gate on every read; nothing consumed on read; `private, no-store`, `noindex`.
- No server-side audio decoding (no codec attack surface); the browser's decoder plays the file from `usercontent.zaa.gl` under the
  sandbox CSP and `nosniff`.
- Timeline text is data; owner routes 404 for others; failing credential 401.

## Retention
Content retention (Free 7, Starter 30 days); timeline bytes count toward the quota. `BriefKindHandler` deletes the row at purge; the parts
follow their own lifecycle.

## What the UI needs
1. **Recorder** (on an image or PDF in the owner's library): "Record brief" button, microphone permission prompt, 3-2-1 countdown, a
   visible timer that stops at the plan limit (30/60 s), the subject displayed full-width with the pointer captured as normalised x,y
   (mouse/touch), tap = click, a "note" tool (click + type ≤ 140 chars), page buttons for PDFs (pdf.js), stop → review playback with the
   pointer replay → upload the audio with tus (`kind=file`, the MediaRecorder blob), wait for `ready`, then `POST /api/briefs` with the
   measured `durationMs` and the timeline; title/expiry/password/public fields; share URL with `#k=`.
2. **Listener page `app.zaa.gl/vb/<code>`**: the subject (image via `inlineUrl`/`previewUrl`, PDF via pdf.js from `inlineUrl`), an audio
   player (`inlineUrl`), the pointer replayed in sync with `audio.currentTime` (interpolate between `point` events, pulse on `tap`, show
   notes for ~2 s, switch pages on `page`, hide on `hide`), scrubbing, playback speed, captions area reserved, `incomplete` notice,
   password form, 404/410 states. Text as text.
3. Owner card: duration, event count, part states, delete, renew.

## Open questions
1. Verify the real duration server-side (would need a WebM/MP4/MP3 duration parser in the worker) or keep the byte cap?
2. Allow guests (would need guest-owned parts; proposed: no)?
3. Other document types (Office) need a server-side PDF conversion: out of scope; PDF only for now.
4. Transcription/captions (AI module) as a later add-on?
