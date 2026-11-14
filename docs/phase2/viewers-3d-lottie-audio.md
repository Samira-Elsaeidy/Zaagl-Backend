# E2.2 Browser viewers for 3D (`.glb`/`.gltf`), Lottie (`.json`) and audio

PRD: "Let clients rotate `.glb` 3D models, scrub Lottie animations, or listen to audio stems in the browser without installing
heavy software."

Backend scope: accept these types through the **existing** upload pipeline (tus or `/api/uploads/simple`, scan, stored objects,
delivery tickets), detect them by **content** (never the declared type or the name), validate their structure, classify a
**viewer** for them, and serve them by ticket with a safe inline policy. No new upload route, no new link kind: they are
`file` resources (viewer `app.zaa.gl/f/<code>`), and the file descriptor gains `viewer` and `inlineUrl`.

## Detection and validation
Two stages, both fail-closed:
1. **Sniff at `/complete`** (first 64 KB, `MediaSniffer.DetectViewerType`, called from `FileTypePolicy.Classify`):

| Content | Rule | Detected type |
|---|---|---|
| glTF binary | bytes 0-3 `glTF`, version (uint32 LE) = 2, declared length ≥ 20, first chunk type `JSON` | `model/gltf-binary` (`.glb`) |
| glTF JSON | JSON object (after optional BOM/whitespace) whose head contains `"asset"` and `"version"` | `model/gltf+json` (`.gltf`) |
| Lottie | JSON object whose head contains `"layers"`, `"fr"`, `"ip"`, `"op"` | `application/json` (`.json`), viewer candidate `lottie` |
| MP3 | `ID3` tag, or an MPEG audio frame sync (`FF Ex/Fx`, valid layer and bitrate bits) | `audio/mpeg` |
| WAV | `RIFF....WAVE` | `audio/wav` |
| FLAC | `fLaC` | `audio/flac` |
| Ogg | `OggS` | `audio/ogg` |
| AAC (ADTS) | `FFF1`/`FFF9` | `audio/aac` |
| M4A | `ftyp` box with brand `M4A ` or `M4B ` | `audio/mp4` |

A declared `media` upload of these types is re-kinded to `file` exactly like Phase 1 (the media allowlist stays images only).
2. **Full validation in the scan task** (worker, after hashing, before the malware scan; `ViewerValidator`), on the whole
   stored file with hard bounds (`Viewers:MaxJsonBytes`, default 16 MiB of JSON; `JsonDocument` max depth 128):
   - **GLB**: the header length equals the file size; chunk 0 is `JSON` and lies inside the file; the JSON is an object with
     `asset.version` = `"2.0"`; every `buffers[].uri` and `images[].uri` is **absent or a `data:` URI**.
   - **glTF JSON**: same JSON rules (every `uri` a `data:` URI): a `.gltf` must be self-contained.
   - **Lottie**: an object with numeric `fr`, `ip`, `op`, `w`, `h` (1..16,384) and an array `layers`; every `assets[]` entry
     with an image path is **embedded** (`e = 1` or `p` starts with `data:`); **no expressions anywhere** (an `"x"` property
     whose value is a string: Lottie expressions are JavaScript that players evaluate).
   - **Audio**: the magic is enough (the browser decoder is the parser; nothing executes).

   Outcome: valid → `storage_objects.viewer` = `model3d` | `lottie` | `audio`. A 3D or Lottie file with **external references
   or expressions is rejected** (`rejected_reason = unsafe-content`, the link shows the normal `422 content-rejected`). A JSON
   file that only looked like Lottie but is not one keeps no viewer and is served as `application/octet-stream` attachment.

Server-side rejection is the primary control; the viewer rules below are defence in depth.

## Delivery
- The file descriptor (`GET /api/public/content/{code}`, kind `file`) gains `viewer` (`model3d|lottie|audio|null`) and, for a
  viewer type that is not burn-after-download, an **inline ticket** (`inlineUrl`, `Content:InlineTicketMinutes`). The download
  button and its POST are unchanged (burn files never get an inline ticket, addendum §8.1.3).
- `usercontent.zaa.gl` serves the bytes with the **detected** type (`model/gltf-binary`, `model/gltf+json`,
  `application/json`, `audio/*`), `Content-Disposition: inline` only for an Inline ticket of a validated viewer type
  (`ObjectDelivery.ViewerInlineTypes`), and always the §4.4 header set: `nosniff`, `sandbox; default-src 'none'` CSP,
  `private, no-store`, `Range` support (audio seeking), and `X-Zaagl-Acao` so the viewer on `app.zaa.gl` may `fetch()` it.
  None of these types can execute in a browser tab: JSON and glTF render as text or download, audio plays in the built-in
  player inside a sandboxed document.

## Viewer rules for the UI (MUST)
1. Fetch bytes only from the descriptor's `inlineUrl`; never follow URLs found inside the file.
2. 3D: use a glTF loader with a custom resource loader that **refuses every non-`data:` URI** (three.js `LoadingManager`
   `setURLModifier` returning a blocked URL, or `<model-viewer>` with a CSP `connect-src` limited to usercontent).
3. Lottie: use a renderer **without expression support** (`lottie-web` `lottie_light` build, or dotLottie with expressions off)
   and block image asset URLs that are not `data:`.
4. Audio: `<audio controls preload="metadata" src=inlineUrl>`; waveform drawing client-side from the fetched bytes.
5. The app's CSP keeps `connect-src`/`media-src` to `usercontent.zaa.gl` (ADR-0023), so even a missed reference cannot reach
   a third-party host.

## Data model
Expand-only: `files.storage_objects.viewer smallint null` (`1 model3d`, `2 lottie`, `3 audio`) with a check `viewer IN (1, 2, 3)`
(Files migration `AddViewerKind`). `StoredObjectSummary` gains a trailing optional `Viewer`.

## Plan gating
All plans including guests, under the normal upload rules (size, quota, scan, guest counters). PROPOSED; nothing new to gate.

## Endpoints
No new routes. Changed: `GET /api/public/content/{code}` (file block gains `viewer`, descriptor gains `inlineUrl` for viewer
types), `GET /api/resources/{id}` (owner detail gains `viewer`), `/d/<ticket>` (inline disposition for validated viewer types).
Code: `src/Modules/Files/Application/MediaSniffer.cs`, `ViewerValidator.cs`, `ScanTaskHandler.cs`,
`src/Zaagl.Api/Infrastructure/ObjectDelivery.cs`, `src/Zaagl.Api/Endpoints/PublicContentEndpoints.cs`. Tests:
`tests/Zaagl.Files.Tests/ViewerTypeTests.cs` (sniffing and validation), `tests/Zaagl.Api.Tests/Phase2/ViewerUploadsTests.cs`.

## What the UI needs
`app.zaa.gl/f/<code>` switches on `file.viewer`: `model3d` → orbit viewer (rotate, zoom, reset, wireframe toggle, background
toggle, "download original"); `lottie` → player (play/pause, scrub bar with frame counter from `ip`/`op`/`fr`, loop toggle,
background colour); `audio` → player (play/pause, seek, volume, waveform). Every viewer has a "processing" state (scan not
done), the `422 content-rejected` state ("this file references external content and was not accepted"), the burn state (no
viewer, download button only) and the standard 401/404/410 states.

## Open questions
1. Accept `.gltf` with external buffers by asking for a zip bundle (`.zip` with `.gltf` + `.bin` + textures)? Proposed: later.
2. dotLottie (`.lottie`, a zip) support? Proposed: later (needs zip inspection).
3. Should audio and 3D count as "media" (PD-16, media allowlist) instead of "file"? Proposed: stay `file` until PD-16 is decided.
