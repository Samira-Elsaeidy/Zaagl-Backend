# E2.2 Auto-design loupe (palette, dimensions) and pinned comments

PRD: "Display the color palette (HEX/RGB) and dimensions next to shared artwork, and let clients click anywhere on the image to
leave pinpointed feedback."

## Palette and dimensions
- **Dimensions** already exist (Phase 1 probe: `media.width`/`media.height`, after EXIF orientation).
- **Palette**: computed by the worker's `media-preview` task right after it makes the `thumb-320` thumbnail, **from the thumbnail**
  (≤ 320 px a side), never from the original: the decode is small and bomb-safe, and the original is never touched.
  `PaletteExtractor` (`src/Modules/Files/Application/PaletteExtractor.cs`): fixed sampling grid of at most 4,096 opaque pixels
  (alpha ≥ 128), 12-bit colour histogram, weighted k-means (k ≤ 6, farthest-point seeding with index tie-breaks, 12 iterations,
  "redmean" perceptual distance). **Deterministic**: the same image always yields the same palette. Bounded by the task's
  existing wall-clock limit (`Media:TimeoutSeconds`).
- Stored as `files.media_items.palette jsonb` (expand-only migration `AddMediaPalette`), exposed as `media.palette` in the owner
  detail/list and the public media descriptor: `[{hex:"#4682B4", r:70, g:130, b:180, share:0.62}, ...]`, largest share first.
- Images processed before this release have `palette: null` until they are re-processed (open question: a one-off backfill).
- Animated images: the palette of the first frame (the thumbnail's frame).

## Pinned comments
Comments are pinned at a **normalised point** `(x, y)` in `0..1` of the displayed image (after EXIF orientation), so they stay
put at any zoom. Targets: an image (`media`), a comparison (`compare`, then `side` = `before|after` is required) or an approval
draft (`approval`, feature 7). Each target has its own opt-in.

### Data model (`collab`)
`comment_settings`: `resource_id` PK, `owner_id`, `enabled`, `allow_anonymous`, `comment_count` (0..10,000), timestamps.
`comments`: `id` bigint, `resource_id` (FK cascade to settings), `x`, `y` (checked 0..1), `side` (null | 0 | 1), `body`
(1..1,000 characters), `author_kind` (0 owner, 1 signed-in visitor, 2 anonymous), `author_user_id` (null only for anonymous,
checked), `author_name` (≤ 60, visitors only), `status` (0 open, 1 resolved, with `resolved_at`), `created_at`.
No IP, no user agent, no email is stored.

### Rules
- **Who can read**: anyone who passes the target link's gate (state, `#k=` link key, password grant), exactly like viewing it.
- **Who can post**: the owner, and visitors holding the link, **only after the owner turned comments on** for that resource
  (`PUT /api/resources/{id}/comments/settings`, plan `Tools.CommentsEnabled`). Anonymous visitors only when
  `allowAnonymous`, and they must pass the spec §7.12 CAPTCHA (action `guest-comment`) and the per-minute brake. The settings
  are checked **before** the CAPTCHA: comments off is `403`, an anonymous visitor where anonymous posting is off is `401` "Sign in
  to comment on this content." (never `400 captcha-required`), and such a request spends no CAPTCHA verification and no guest-brake
  slot. The website shows the Turnstile widget only to an anonymous visitor on a thread with `allowAnonymous: true`.
- **Coordinates**: `x` and `y` are required JSON numbers in `0..1`; a missing or `null` coordinate is `400` (it used to pin at 0),
  as are numbers sent as strings (`"x": "0.5"`), `NaN`/`Infinity` and out-of-range values.
- **Limits**: per-IP `comment-write` limiter (`RateLimits:CommentWritePerMinute`, 20), the guest brake for anonymous visitors,
  `Comments:MaxPerResource` (500) enforced under the settings row lock (no race past the cap), 16 KB request body.
- **Content**: plain text, trimmed, 1..1,000 characters, no control characters except newline, NUL refused at the edge. Never
  HTML: stored as typed and returned as data; the UI renders it escaped (a body like `<img src=x onerror=...>` is just text).
- **Moderation**: the owner lists everything, resolves/reopens and deletes any comment; a signed-in author may delete their own.
- **Target must be ready** and owned by an account (guest content has nobody to moderate it: 404).

### Endpoints
Code: `src/Zaagl.Api/Endpoints/CollaborationEndpoints.cs` (`MapComments`), `src/Modules/Collaboration/Application/CommentService.cs`.
Tests: `tests/Zaagl.Api.Tests/Phase2/PaletteAndCommentsTests.cs`, `tests/Zaagl.Files.Tests/PaletteTests.cs`.

| Method and route | Who | What |
|---|---|---|
| `GET /api/public/comments/{code}` | link holder | `{enabled, allowAnonymous, count, maxComments, items:[{id, x, y, side, body, author, authorName, status, createdAt, resolvedAt, mine}]}` |
| `POST /api/public/comments/{code}` | link holder (owner, signed-in or anonymous with `Captcha-Token`) | `{x, y, body, side?, authorName?}` (x, y required numbers) → `201 CommentDto`; `400` invalid body, `403` comments off, `401` anonymous not allowed (checked before the CAPTCHA), `409 limit-reached`. |
| `DELETE /api/public/comments/{code}/{commentId}` | signed-in author | Own comment only (`404` otherwise). |
| `PUT /api/resources/{id}/comments/settings` | owner (`tools:write`) | `{enabled, allowAnonymous}` → `CommentSettingsDto`. |
| `GET /api/resources/{id}/comments` | owner (`resources:read`) | Full thread. |
| `POST /api/resources/{id}/comments/{commentId}/resolve` and `/reopen` | owner (`tools:write`) | → `CommentDto`. |
| `DELETE /api/resources/{id}/comments/{commentId}` | owner (`tools:write`) | Moderation delete. |

### Security and abuse
- IDOR: owner routes are owner only (404); public routes are scoped by the gate of the code, and a comment id from another
  resource is 404.
- Spam: opt-in per resource, CAPTCHA and brakes for anonymous visitors, per-resource cap, owner delete. Optional (open):
  notify the owner of new comments.
- Privacy: no IP stored; an anonymous author is not identifiable afterwards.

### Retention
Comments live as long as their target: when Resources reclaims the image/comparison/approval (purge, guest expiry, orphan
sweep), `CommentPurgeHandler` deletes its settings and comments (cascade). Account deletion: open question (anonymise the
author's comments on other people's content, or delete them).

## Plan gating (PROPOSED)
Palette: every plan (computed for every image). Turning comments on: Free and Starter (`Tools.CommentsEnabled`); posting is open
to every link holder the owner allows.

## What the UI needs
1. **Loupe panel** next to an image (viewer `/i/<code>`, comparison `/c/<code>`, approval `/a/<code>`): dimensions, palette
   swatches with HEX and RGB, copy-to-clipboard, share percentage bar; "palette not available" when null.
2. **Comment mode**: a toggle; click on the image places a pin at normalised `(x, y)` (for comparisons on the side clicked); a
   popover with the text box (1,000 characters), optional display name for visitors, Turnstile for anonymous visitors; pins
   numbered in creation order; resolved pins dimmed; a side list of comments; states: comments off (hide the toggle), sign-in
   required (`401`), limit reached (`409`), rate limited (`429`).
3. **Owner controls**: "Allow comments" and "Allow anonymous comments" switches, resolve/reopen and delete on each comment,
   "show resolved" filter.

## Open questions
1. Threads (replies) on a pin? Proposed later (one level).
2. Email the owner on new comments (spec §7.10 email rules)?
3. What happens to a deleted account's comments on other people's content?
4. Palette backfill for images uploaded before this release?
