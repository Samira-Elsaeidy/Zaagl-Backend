# ARC-011 · Media fidelity and processing

| | |
|---|---|
| Owner | zaagl-architect. Implementers: backend (Media module: BE-015, BE-016; delivery branch in BE-017), frontend (image viewer FE-015, video player FE-016), devops (worker image: libvips, librsvg, FFmpeg). |
| Status | DESIGN. **Slice 1 (plain images) needs only §2, §3 (rows "original"), §4 (interim) and §9;** everything else is slice 3 and waits for the open decisions. Contracts: `src/Modules/Media/Contracts/MediaContracts.cs` (stub, unchanged by this note). |
| Rules | Addendum §4.4, §4.3, §6.7, §7, §8.1.3, §9. PD-15, PD-28, PD-37, PD-45 (APPROVED). |
| OPEN, not decided here | **PD-13** EXIF/GPS, **PD-16** formats, **PD-18** SVG (PROPOSED), **PD-30** video (PROPOSED), **PD-31** PDF preview (PROPOSED). Each has an **interim default** marked **(interim)**; interim defaults are configuration, not decisions. |
| Related | ARC-009 (pipeline, promote), ARC-008 (inline allowlist, headers), ARC-012 (`media-preview` task), ARC-019 (burn files get no derivative), `openapi-phase1.yaml` (`MediaDto`, `MediaDerivativeDto`). |

## 1. Principle

"100% original": the bytes a viewer or a downloader receives for the original are **the bytes the owner uploaded**, the SHA-256 of the upload equals the SHA-256 of what is served
(FEATURES_PLAN Step 4 acceptance). Everything the product shows that is not that file is a **derivative** with its own id, its own storage area, its own ticket, and a label.

## 2. Original immutability (invariants, each testable)

| # | Invariant | Mechanism | Test |
|---|---|---|---|
| I1 | The original is never opened for write after promote | the object file is `0640`; Media and the delivery path open it `FileAccess.Read`, `FileShare.Read`; the worker has no code path that writes under `objects/` except `MoveAsync` | unit: every `IObjectStorage` use in Media is `OpenReadAsync`/`StatAsync`; integration: file mtime and hash unchanged after the full pipeline |
| I2 | `storage_objects.sha256` is computed once, in the scan task (ARC-009 §7 step 3), from the stored bytes | never recomputed by Media | QA-024: upload -> download by owner ticket -> hash equal |
| I3 | Derivatives never replace the original in any response | `Preview`/`Inline` tickets name either the original or an explicit derivative kind; the viewer at 100% zoom shows the original | viewer test: the `<img src>` ticket decodes to derivative `null` |
| I4 | A failed derivative never fails the resource | `media-preview` failure -> derivative `failed`, resource stays `ready`, original served | task failure test |
| I5 | Burn files and `processing`/`rejected` resources get no derivative and no inline/preview ticket | ARC-019, addendum §8.1.3 | descriptor test |
| I6 | Derivatives live only in `previews/` and `thumbnails/` (regenerable, not backed up, DEVOPS_PLAN §2.2) and are deleted with the resource | `OnResourceDeletedAsync` queues `delete-bytes` per derivative key | purge test |

## 3. What is stored and shown

| Thing | Where | Served as | Slice |
|---|---|---|---|
| Original | `objects/` (C7) | `Inline` ticket for the allowlisted raster types (viewer, 100%), `Download`/`Owner` attachment | 1 |
| `thumb-320` (library cards) | `thumbnails/` | `Preview` ticket, inline, `image/webp` (PNG when alpha) | 3 |
| `preview-1600` (og:image for **public** images, large cards) | `previews/` | `Preview` ticket, inline | 3 |
| `poster` (video, animated image first frame) | `thumbnails/` | `Preview` ticket | 3 |
| `preview-mp4` (non-browser-playable video, PD-30) | `previews/` | `Preview` ticket, `video/mp4` | 3, PROPOSED |
| `svg-raster` (PD-18) | `previews/` | `Preview` ticket, `image/png`, only inside `<img>` | 3, PROPOSED |

Kinds are exactly `DerivativeKinds` (`MediaContracts.cs`). There is **no `svg-safe` derivative** (the sanitised-SVG copy of DATABASE_AND_STORAGE §4.9 is dropped: it adds a markup attack surface for no viewer benefit, PD-18 recommends the raster; reopen only if the product owner wants vector zoom).

### 3.1 Metadata (`media.media_items`, written by the probe after `ready`; slice 3)

`media_type` (image, animated-image, svg, video), `width`, `height` **as displayed** (after EXIF orientation), `orientation` (1-8, original value), `frame_count`, `duration_ms`, `has_alpha`, `color_space`, `icc_profile_name`, `video_codec`, `audio_codec`, `browser_playable`, `has_gps_metadata` (PD-13, owner-only, never public).
Probes read the original read-only (I1), by path, with the limits of §6. A probe failure leaves the fields null; the viewer then relies on the browser.
**Slice 1 has no `media` schema:** `IMediaReader` is a stub that maps the detected MIME to `MediaType` (`image/gif` and animated PNG/WebP are reported as `animatedImage` only when known, otherwise `image`) with every other field null; dimensions come from the browser (`naturalWidth`). BE-015 replaces the stub and **backfills** by enqueueing a probe task per existing media resource (the probe is idempotent).

## 4. EXIF and GPS (PD-13, OPEN)

**(interim, slice 1 and 3)** Originals are kept byte-for-byte; no stripping, no option, nothing in the API or UI promises removal of location data. `has_gps_metadata` is computed in slice 3 for the owner only and not shown to visitors. Derivatives (thumb, preview) are always written without metadata and rotated upright.

Options for the product owner (PD-13), with their consequences:

| | A. Untouched only (interim) | B. Untouched default plus an opt-in "remove location data" | C. Strip by default |
|---|---|---|---|
| SHA-256 served = uploaded | always | **not** when the owner opts in (the stored original is the stripped file; both hashes are recorded) | never |
| Method | none | **lossless** removal, never re-encoding pixels: JPEG: drop APP1 (EXIF/XMP) segments; PNG: drop `eXIf`/`tEXt`/`iTXt` chunks; WebP: drop `EXIF`/`XMP ` RIFF chunks; GIF: none. libvips `strip` re-encodes JPEG and is **not** acceptable | same |
| Where in the pipeline | n/a | after hash, before scan; stored bytes and `sha256` are the stripped file, `original_sha256` kept for evidence | same |
| API/UI | none | additive `stripLocation: boolean` (default false) in `CompleteUploadRequest`; desktop clipboard share defaults it to true | none |
| Privacy | GPS visible to anyone who holds the link | owner decides | safest, conflicts with "100% original" |

Not built, not in the OpenAPI wire, until PD-13 is answered. Recommendation: B.

## 5. SVG (PD-18, PROPOSED; slice 3)

**(interim)** SVG is **not accepted as media** in slice 1 and 2 (`type-not-allowed` for `media`, re-kinded to `File` from slice 2: it downloads as an attachment, never renders).
If PD-18 is approved:

- Original stored byte-for-byte, **only ever an attachment** (addendum §4.4); never inline, never in `<object>`/`<embed>`/`<iframe>`.
- Viewer shows `svg-raster` (PNG) in an `<img>`, labelled "rendered preview", with "Download original (.svg)".
- Detection: after an optional BOM, XML declaration and comments the root element is `svg` in the SVG namespace; a `DOCTYPE` with an internal subset or any `<!ENTITY` is refused (`type-not-allowed`: entity expansion and XXE), not sanitised.
- Rasteriser: libvips/librsvg through NetVips with **no external resource loading, no network, no `<script>`, no `foreignObject`**, entity expansion disabled, input at most 5 MB (config), output at most 4096 px on the long side, 20 s timeout, in the worker cgroup. Any failure -> derivative `failed`, original still downloadable.
- Tests (QA-022): script, `onload`, `javascript:` href, external `href`/`xlink:href`, `foreignObject`, entity bomb, XXE file read, 10 MB path data, nested `<use>` bomb, SVG inside SVG: the raster is produced or refused, nothing executes, no outbound request (assert on a local listener), no file read.

## 6. Raster processing limits (NetVips, slice 3)

- `thumbnail` from the file **path**, `access: sequential`, `fail_on: error`, input at most `Media:MaxPixels` = 100,000,000 pixels (decided from the **header** before decode); above it the derivative is `skipped`, the original stays downloadable (DATABASE_AND_STORAGE §4.8). 30,000 x 30,000 PNG (900 MP) must be `skipped` in under 2 s and use under 200 MB RSS (QA-023).
- Output: WebP quality 80, or PNG when `has_alpha`; metadata stripped; colour converted to **sRGB** for derivatives only (the original keeps its ICC profile, 16-bit depth, CMYK, wide gamut); EXIF orientation applied (`autorot`) in derivatives only.
- Animated GIF/WebP/APNG: original served as is (animation preserved); `poster` = first frame; `frame_count` recorded; derivatives are single-frame.
- Concurrency `Media:MaxParallel` = 2, per-image timeout 30 s, memory limit through the worker cgroup (ARC-012 §5); `media-preview` retries 3 times then `failed` (ARC-012 §4).
- **(interim, technical default)** the viewer shows the original inline only when the probe reports at most `Media:MaxInlinePixels` (100,000,000); above that it shows "Download original" with the thumbnail, because a browser tab can fail on a 900 MP image. Before the probe exists (slice 1) the viewer always tries the original.

## 7. Video (PD-30, PROPOSED; slice 3, FE-016)

**(interim)** No video in slice 1 or 2 (`type-not-allowed` for `media`; a video uploaded as `file` is just a file).
If PD-30 is approved as proposed:

- Accepted containers by sniff: MP4/MOV, WebM, MKV. `ffprobe -v error -show_streams -show_format -of json -protocol_whitelist file`, timeout 60 s, run at `nice 10` in the worker cgroup, **path input only** (no URL), no HLS/concat/playlist demuxers (a text file that FFmpeg treats as a playlist is the classic SSRF/file-read vector): reject when the probed format is not one of the accepted names.
- `browser_playable` = H.264/AAC in MP4, or VP8/VP9/AV1 with Opus/Vorbis in WebM. Playable originals are served **as is** inline with `Range` (`<video>`), like images.
- Not playable (HEVC, ProRes, MKV with unusual codecs): `preview-mp4` (H.264 baseline/main + AAC, longest side 1280, CRF 23, `+faststart`) generated by FFmpeg with a hard wall-clock cap (`Media:VideoTimeoutSeconds`, proposed 10 minutes) and output cap; the original stays downloadable. Concurrency 1 (`Media:MaxParallelVideo`).
- `poster` frame at 1 s (or the first frame when shorter).
- Not decided: maximum duration per plan (PD-30 question), whether the transcoded preview counts toward quota (see §10), audio-only files.
- Viewer states: `processing` (scan), `preparing preview` (transcode pending; the original download is offered), `failed preview` (download only).
- Tests (QA-023): HEVC MOV, a 100 MB file with a 4-hour duration, a crafted HLS-text file named `.mp4`, a truncated file, zero-frame file.

## 8. Delivery of originals and derivatives (summary of ARC-008, no change)

Inline only for `Content:InlineMimeAllowlist` types (PNG, JPEG, GIF, WebP, MP4, WebM proposed; the final list is PD-16); `Content-Type` from detection; sandbox CSP and `nosniff` on every response; SVG, PDF, HTML, XML and unknown types are attachment. Derivative tickets use purpose `Preview` with `derivative = <kind>`; the original in the viewer uses `Inline` with no derivative. Tickets are 15 minutes (`Content:InlineTicketMinutes`); a stale ticket is recovered by refetching the descriptor once (ARC-008 §3).

## 9. Image viewer contract (FE-015, slice 1)

The descriptor for `kind = media` in state `ready` carries `inlineUrl` (original, `Inline` ticket), `mime`, `sizeBytes`, `filename`, `expiresAt`, `hasPassword`, `media` (stub: `mediaType`, `detectedMime`, rest null in slice 1). The viewer:

- renders `<img src=inlineUrl>` at natural size (100%) by default; "fit to screen" and zoom are CSS on the same element (never a second request, never a derivative); a checkerboard/light/dark background switcher is purely client side (works without `hasAlpha`);
- offers "Download original" through `POST /api/public/content/{code}/download` (a `Download` ticket, attachment, never burns an image because burn is files only);
- states: `processing` (retry 1 s, at most 5 times then manual refresh), `rejected` (infected: "this file was blocked"; `scan-unavailable`: "scanning failed, please upload again"; owner only sees the reason, a visitor sees a neutral message), `410` pages from ARC-003 §4.2, password gate;
- sends no referrer and no credentials to the ticket host (the `<img>` uses `referrerpolicy="no-referrer"`; `connect-src`/`img-src` allow `https://usercontent.zaa.gl`, ADR-0023).

## 10. Quota and capacity

**(interim)** Only originals count toward the owner's quota (`ContentQuota`); derivatives are regenerable, sized separately (previews + thumbnails about 15% of disk, DEVOPS_PLAN §2.1) and not charged. Open for the product owner: should preview-mp4 count? Recommendation: no, because they are deleted and rebuilt, and Phase 1 maximum is 100 MB per original.

## 11. Decided, interim and open

| Item | Status |
|---|---|
| Originals byte-for-byte, derivatives separate, no derivative for burn files | decided (addendum §6.7, §8.1.3) |
| EXIF/GPS | **OPEN (PD-13)**, interim untouched |
| Format allowlist beyond PNG/JPEG/GIF/WebP | **OPEN (PD-16)**, interim not accepted as media |
| SVG | **PROPOSED (PD-18)**, interim not accepted as media |
| Video, transcoding, max duration | **PROPOSED (PD-30)**, interim not accepted as media |
| PDF preview | **PROPOSED (PD-31)**, FE-018, slice 2 uses a plain download page (no preview) |
| Derivatives not charged to quota; 100 MP inline cap; `svg-safe` dropped | technical defaults of this note (configuration, reversible) |

## 12. Review checklist for BE / FE / OPS

- [ ] No code path writes to `objects/` except `MoveAsync`; Media opens originals read-only.
- [ ] A served original hashes to `storage_objects.sha256` (integration test, slice 1).
- [ ] Derivatives: own ids, `previews/`/`thumbnails/`, deleted with the resource, never replace the original, never exist for burn files.
- [ ] Probe and thumbnail decisions use header values before decoding; the bomb corpus passes.
- [ ] FFmpeg and librsvg run path-only, no network, in the worker cgroup; playlist demuxers refused.
- [ ] The viewer never fetches a derivative at 100%; zoom and background are CSS.
- [ ] `has_gps_metadata` is never in a public response.

## 13. Questions for the user

1. **PD-13** EXIF/GPS: A, B (recommended) or C above.
2. **PD-16** formats: approve PNG, JPEG, GIF, WebP in the first release and defer AVIF, HEIC, SVG and video to a later release, or require them before launch.
3. **PD-18** SVG: approve "raster preview, original attachment only" (recommended).
4. **PD-30** video: transcode only non-browser-playable formats (recommended); maximum duration per plan; whether previews count toward quota.
5. **PD-31** PDF preview: `react-pdf` (browser) or worker-rendered page images; slice 2 ships without preview either way.
