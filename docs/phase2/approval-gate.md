# E2.2 Watermark and client approval gate

PRD: "Overlay a protective watermark on creative drafts until the client clicks 'Approve' or completes payment."
Payments are **not built**; the gate is a state machine with a payment seam a provider can be plugged into later.

## Model
An approval is a **content link of kind `approval` (7)** (viewer `app.zaa.gl/a/<code>`) whose row in `collab.approvals` points at
one of the owner's ready raster images (the **draft**, uploaded through the normal path). The client receives only the approval
link; the draft's own link is never shared by this feature.

`collab.approvals`: `resource_id` PK, `owner_id`, `draft_resource_id`, `watermark_text` (1-60 characters, default `DRAFT`),
`watermark_kind` (the Files derivative kind `wm-<16 hex of SHA-256(text)>`), `release_on_approval` (default true), `state`
(0 draft, 1 approved, 2 paid, 3 released), `approved_at`, `approver_name` (≤ 100), `approver_email` (≤ 254), `approval_note`
(≤ 1,000), `approver_user_id` (when signed in), `paid_at`, `payment_provider`, `payment_reference`, `released_at`, `created_at`.
Check constraints encode the state invariants (approved ⇒ `approved_at`, paid ⇒ `paid_at`, released ⇔ `released_at`).

### State machine (`ApprovalStateMachine`, total, unit-tested)
```
draft --approve--> approved --release--> released
draft --pay------> paid ------release--> released
approved --pay---> paid
```
With `release_on_approval` (default) the approve or pay step goes straight to `released` in the same UPDATE. Every transition is
**one conditional UPDATE on the expected state**, so parallel approvals produce exactly one transition (tested with 8 parallel
POSTs: one `approvedAt`, one approver). Nothing ever goes from `draft` to `released` without approve or pay.

### The watermark (Files, worker)
- At creation the module asks Files (`IWatermarkDerivatives.RequestAsync`, contract in `Files/Contracts/FileContracts.cs`) to
  render the watermark; the worker task `watermark` (`WatermarkTaskHandler`) reads the **original read-only** through the same
  bomb-safe decode as thumbnails (`ImageThumbnailer`, `Media:MaxPixels`/`MaxDecodePixels`, wall-clock limit), scales it to at most
  **1,600 px** on the long edge, and burns in two layers (`WatermarkRenderer`): a diagonal stripe lattice that needs **no font**
  (so the copy is marked even on a host without fonts) and the tiled text when a font is available. Output: WebP, EXIF stripped,
  stored as a derivative in `previews/` (`files.media_derivatives`, kind `wm-…`). The original is never modified (addendum §6.7).
- Before release the descriptor carries **only** a Preview ticket on that derivative (`preview.inlineUrl`), `previewState` =
  `processing` until the worker is done, `unavailable` if the draft image is gone or the render failed. The original is never
  ticketed and `POST .../download` is `403` before release.
- After release the descriptor carries the **original** (inline ticket) and `POST /api/public/approvals/{code}/download` mints a
  normal download ticket (120 s) for it.

### Payment seam (not built)
`ApprovalService.MarkPaidAsync(resourceId, provider, reference)` performs `draft|approved → paid` (or straight to `released`
with release-on-approval), idempotent per `payment_reference`. A provider integration would add: a checkout-session route on the
approval link, a signed webhook (verified like the Stripe one in Billing) that calls `MarkPaidAsync`, and a price on the approval
row. No route calls it today; tests exercise it directly.

## Endpoints
Code: `src/Zaagl.Api/Endpoints/CollaborationEndpoints.cs` (`MapApprovals`), `src/Modules/Collaboration/Application/ApprovalService.cs`,
`src/Modules/Files/Application/WatermarkTask.cs`. Tests: `tests/Zaagl.Api.Tests/Phase2/ApprovalGateTests.cs`.

| Method and route | Who | What |
|---|---|---|
| `POST /api/approvals` | JWT or API key `tools:write` (Starter) | `{draftResourceId, watermarkText?, releaseOnApproval? (true), title?, expiresAt?, password?, customSlug?, public?}` → `201 {resourceId, code, shortUrl, linkKey?, expiresAt}` |
| `GET /api/approvals/{id}` | owner | State, approval record including `approverEmail` and `approvalNote`, `previewState`. |
| `POST /api/approvals/{id}/release` | owner (`tools:write`) | `approved|paid → released`; `409` otherwise. |
| `DELETE /api/approvals/{id}` | owner | Link deleted (410); the draft image is untouched. |
| `GET /api/public/approvals/{code}` | link holder | `{kind:"approval", title, expiresAt, hasPassword, approval:{state, approvedAt, approverName, paidAt, releasedAt}, previewState, preview, original, canApprove}` |
| `POST /api/public/approvals/{code}/approve` | link holder (signed in, or anonymous with `Captcha-Token`, action `guest-approve`) | `{name?, email?, note?}` → `ApprovalInfoDto`; idempotent. |
| `POST /api/public/approvals/{code}/download` | link holder | After release: `{url, expiresAt}`; before: `403`. |
Pinned comments work on approval links too (`/api/public/comments/{code}`, owner opt-in).

## Security notes
- The original is unreachable before release on every path: no inline, preview or download ticket for it is minted by this
  feature; the derivative ticket names the `wm-…` kind and the hop serves only that file; tickets cannot be forged (HMAC).
- The watermarked copy is downscaled (1,600 px) so it is not a substitute for the original.
- The approval is recorded with time and the optional name/email/note typed by the approver (personal data the owner sees;
  kept with the row until purge). No IP is stored.
- Anonymous approvals require the CAPTCHA and the per-minute brake; bots never POST.
- Watermark text is plain text (≤ 60, no control characters) drawn as pixels.

## Retention
Content retention (Starter 30 days), capped by the draft image's expiry. Purge removes the row (`ApprovalKindHandler`); the
`wm-…` derivative is deleted with the draft image's bytes.

## Plan gating (PROPOSED)
Starter only (`Tools.ApprovalEnabled`; the PRD lists watermarks under Pro).

## What the UI needs
1. **Owner create**: pick/upload the draft image, watermark text, "release automatically when approved" toggle, title, expiry,
   password, alias; share link; status card (draft / approved by *name* at *time* / released), "Release now" button when
   manual, approver email and note, delete.
2. **Client viewer `app.zaa.gl/a/<code>`**: the watermarked preview (spinner while `previewState = processing`), a clear
   "Approve" call to action with optional name, email and note (Turnstile for anonymous visitors), confirmation state; after
   release: the clean original and a "Download" button (POST); when approved but not released: "Approved, waiting for the owner
   to release"; optional pinned comments for feedback before approving; 401/404/410 states.

## Open questions
1. Should approval require the approver to be signed in (stronger record) instead of an optional name/email?
2. Notify the owner by email on approval?
3. Payments: provider, pricing on the link, refunds, and whether "pay" implies "approve".
4. Should a released approval keep serving the original until expiry, or should the owner be able to revoke it?
