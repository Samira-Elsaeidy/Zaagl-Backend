# E2.2 Interactive before/after slider

PRD: "Upload 'before' and 'after' design versions to create an interactive swipe-comparison link for clients."

## Model
A comparison is a **content link of kind `compare` (6)** (viewer `app.zaa.gl/c/<code>`) whose resource row in
`collab.comparisons` points at two existing **image resources** of the same owner. Images are uploaded first through the
normal Phase 1 path (tus or `/api/uploads/simple`, `kind=media`), scanned and probed as usual; the comparison never copies or
touches their bytes. The comparison link has its own code, link key, optional password, alias and expiry.

`collab.comparisons`: `resource_id` PK (the comparison's resource), `owner_id`, `before_resource_id`, `after_resource_id`
(different, both checked at creation), `before_label`, `after_label` (≤ 40 characters, plain text), `created_at`.
No cross-schema foreign keys (module rule); the parts are re-checked on every read.

## Rules
Creation (`CompareService.CreateAsync`), all checked before anything is written:
1. Plan `Tools.CompareEnabled` (402 otherwise); accounts only (guests have no owned images to pair).
2. Each part: exists, `kind = media`, owned by the caller (any other case is `422 resource-unusable`, never a hint of
   existence), processing `ready`, its own link `active`, not burn-after-download, scanned clean, detected type a raster image
   on the inline allowlist (`image/png|jpeg|gif|webp`; SVG and anything else refused).
3. Expiry: `min(requested or plan retention, before part's link expiry, after part's link expiry)`: a comparison never
   outlives its images. Password needs `PasswordOnContent` (Starter); `public` is allowed like an image (PD-37).

Reading (`GET /api/public/compare/{code}`): the gate (state, key, password grant); then for each part the authoritative state
is recomputed (part link snapshot + resource processing + object scan). Usable parts get a short-lived **inline ticket** on the
original (`Content:InlineTicketMinutes`) plus the probe dimensions and a `thumb-320` preview ticket when ready; an unusable part
(deleted, expired, taken down, rejected) is returned as `state: "unavailable"` with no URL. Never a download ticket.

The parts' own link keys and passwords are not required: by composing the comparison the owner shares those two images through
it. Taking a part down (abuse/safety) makes it unavailable here within the cache bound, because the ticket hop re-checks the
part's own link on every byte request.

## Endpoints
Code: `src/Zaagl.Api/Endpoints/CollaborationEndpoints.cs`, `src/Modules/Collaboration/Application/CompareService.cs`.
Tests: `tests/Zaagl.Api.Tests/Phase2/CompareSliderTests.cs`.

| Method and route | Who | What |
|---|---|---|
| `POST /api/compare` | JWT or API key `tools:write` | `{beforeResourceId, afterResourceId, beforeLabel?, afterLabel?, title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/compare/{id}` | owner (`resources:read`) | The comparison with both parts' states (owner tickets for preview). |
| `PATCH /api/compare/{id}` | owner (`tools:write`) | `{beforeLabel?, afterLabel?, swap?}`. Title, expiry, public and password go through `PATCH /api/resources/{id}`. |
| `DELETE /api/compare/{id}` | owner (`tools:write`) | Link deleted (410). The images are untouched. |
| `GET /api/public/compare/{code}` | link holder | Descriptor: `{kind:"compare", state, title, expiresAt, hasPassword, before:{label, state, mime, width, height, inlineUrl, previewUrl}, after:{...}}`. `state` is `ready` when both parts are usable, else `incomplete`. |
The owner's list is `GET /api/resources?kind=compare` (Phase 1 library).

## Security notes
- IDOR: a part that is not the caller's is indistinguishable from a missing one (`422 resource-unusable` for both).
- Same owner only: a comparison can never expose someone else's image.
- No bytes on api/app hosts; inline tickets only, served by `usercontent.zaa.gl` with the Phase 1 header set.
- Labels are plain text, length-capped, control characters refused; the UI renders them escaped.

## Retention
The comparison link follows content retention (7 / 30 days) and never outlives its parts. At purge the kind handler removes
the `collab.comparisons` row; the images follow their own lifecycle.

## Plan gating (PROPOSED)
Guests no; Free yes; Starter yes (PRD lists the A/B diff under Pro).

## What the UI needs
1. Create flow: pick or upload two images ("before", "after"), labels, optional title, expiry, password (Starter), alias,
   public toggle; errors `422 resource-unusable` (image not ready, not an allowed type) and `402`.
2. Viewer `app.zaa.gl/c/<code>`: draggable divider (vertical by default; horizontal and "toggle" modes are client choices),
   labels on each side, zoom/pan, natural-size alignment using `width`/`height` (images of different sizes: fit both to the
   same box), loading, `incomplete` state (one side unavailable), password form, 404/410 states. Optional pinned comments
   (see palette-and-comments.md) on top.
3. Owner page: both thumbnails, labels editable, "swap sides", share URL, delete.

## Open questions
1. Allow more than two images (a "versions" carousel)?
2. Should a comparison keep its parts alive (extend their expiry) instead of being capped by them?
