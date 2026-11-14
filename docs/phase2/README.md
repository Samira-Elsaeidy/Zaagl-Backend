# Phase 2 backend: E2.1 developer tools and E2.2 artist/designer tools

Status: **backend only, built on branch `phase2/backend-e21-e22`, not deployed.** No UI exists yet: every feature note ends
with "What the UI needs" so the owner's wireframes can match the API. Every value marked **PROPOSED** is a proposal that the
product owner has not approved; it is configuration, read by tests from configuration, and listed under "Open questions".

Sources: PRD Phase 2 (`Zaagl-Frontend/src/Doc/zaagl-prd.html`, E2.1/E2.2), spec v9.4, Phase 1 addendum v1.4 (the
rulebook; every rule there still applies), ADR-0001 (link key `#k=`).

| # | Feature | Note | Module (schema) | Link kind (viewer) |
|---|---|---|---|---|
| 2 | .env secret vault (true E2E) | [vault.md](vault.md) | DeveloperTools (`devtools`) | `secret` = 4 (`app.zaa.gl/v/<code>`) |
| 1 | Webhook catcher | [webhook-catcher.md](webhook-catcher.md) | DeveloperTools (`devtools`) | none: `zaa.gl/hook/<token>` |
| 4 | Before/after slider | [compare-slider.md](compare-slider.md) | Collaboration (`collab`) | `compare` = 6 (`/c/<code>`) |
| 6 | 3D, Lottie, audio viewers | [viewers-3d-lottie-audio.md](viewers-3d-lottie-audio.md) | Files (`files`) | `media`/`file` (unchanged) |
| 5 | Palette, dimensions, pinned comments | [palette-and-comments.md](palette-and-comments.md) | Files (palette), Collaboration (comments) | unchanged |
| 3 | Bug capsules | [bug-capsules.md](bug-capsules.md) | DeveloperTools (`devtools`) | `capsule` = 5 (`/b/<code>`) |
| 7 | Watermark and approval gate | [approval-gate.md](approval-gate.md) | Collaboration (`collab`) | `approval` = 7 (`/a/<code>`) |

## Shared design

### Every shareable tool is a content link
Vault secrets, capsules, comparisons and approvals reuse the Phase 1 content model unchanged (addendum §2): the module
registers a `resources.resources` row (`IResourceRegistry`, quota reserved), asks `ILinkIssuer.CreateContentLinkAsync` for the
link (leased-range code, alias rules, denylist, restore freeze, advisory alias lock, link key `#k=`, password, expiry), binds
it and marks the resource ready. Everything that follows comes for free: `IAccessGate` (state, link key, password grant),
`zaa.gl/<code>` redirect to the viewer, takedown and reports by code, the C10 lifecycle outbox, purge, the owner library.

Expand-only schema changes this needs (one migration per owning module):
- `links.links.target_kind` check widened to `0..7` (`Links/Infrastructure/Migrations/*_WidenTargetKindForPhase2Tools.cs`);
- `resources.resources.kind` check widened to `1..7` (`Resources/Infrastructure/Migrations/*_WidenResourceKindForPhase2Tools.cs`).
Both add the new constraint `NOT VALID` and validate it in a separate statement (no long ACCESS EXCLUSIVE lock).

`LinkTargetKind` / `ResourceKind` gain `Secret 4`, `Capsule 5`, `Compare 6`, `Approval 7` (numbers mirror each other as in
Phase 1). `ViewerPathSegments.For(kind)` maps them to `v`, `b`, `c`, `a`; the redirect answers `302 app.zaa.gl/<seg>/<code>`
exactly like Phase 1 content (no fragment added, the browser keeps `#k=`). `GET /api/links?kind=` and the export accept
`secret|capsule|compare|approval`. `GET /api/resources?kind=` accepts the same names.

The webhook catcher is the exception: it is not a share link but a capture endpoint, and lives under the reserved
multi-segment prefix `zaa.gl/hook/` (spec §4.5 rule 6: multi-segment paths are never codes).

### Plan gating (PROPOSED, configuration `Plans:<Plan>:Tools:*`)
The PRD places "webhook catcher, A/B diff, watermarks" in a **Pro** tier, which does not exist in Phase 1 (PD-04: Free and
Starter only, "premium" means a capability flag). Code never compares tiers: it reads `ContentPlan.Tools`
(`Billing/Contracts/ContentPlanContracts.cs`, record `ToolLimits`), resolved per plan in `PlanCatalog` from configuration with
the defaults below. **None of these values is approved**; they are the proposal for the owner.

| | Guest | Free | Starter |
|---|---|---|---|
| Vault: create | yes (CAPTCHA `guest-vault`, PD-14 counters) | yes | yes |
| Vault: ciphertext cap | 16 KB | 64 KB | 256 KB |
| Vault: max views | 1 | 1 | 10 |
| Vault: password gate | no | no | yes (`PasswordOnContent`, PD-54) |
| Webhook catchers live | 0 (accounts only) | 1 | 10 |
| Webhook: requests kept / body bytes stored | - | 100 / 64 KB | 1,000 / 512 KB |
| Webhook: lifetime / ingest rate | - | 24 h / 30 per min | 7 days / 120 per min |
| Before/after slider | no | yes | yes |
| Bug capsules (log cap) | no | yes (512 KB) | yes (2 MB) |
| Pinned comments: owner may enable | no | yes | yes |
| Watermark + approval gate | no | no | yes |
| 3D / Lottie / audio uploads, palette | yes | yes | yes (normal upload limits) |

Expiry of every tool link follows the content retention of addendum §9 (guest 7 d, Free 7 d, Starter 30 d).
Hard ceilings no configuration can exceed are in `Billing/Application/ContentPlans.cs` (`ToolCeilings`).

### Common rules applied to every endpoint
- Owner routes: owner only, `404` for everyone else (admins included, addendum §18); `Cache-Control: private, no-store`.
- Create routes: a failing credential is `401`, never a guest (PD-36, `RejectFailedCredentials`); API keys need `tools:write`
  (new scope, `Zaagl.Api/Infrastructure/ApiScopes.cs`), reads `resources:read`.
- Public routes run `IAccessGate` on every call (state, `X-Zaagl-Link-Key`, `X-Zaagl-Grant`); a wrong key is the same `404`
  as an unknown code. Only `POST` consumes anything (a view, a comment slot, an approval); `GET`/`HEAD` never do.
- Bytes are only ever served from `usercontent.zaa.gl` by ticket (addendum §8); tool descriptors contain inline/preview
  tickets only, never a download ticket.
- Every JSON string is NUL-checked (global `NulRejectingStringConverter`); every cap that must hold under concurrency is a
  conditional `UPDATE` or a row lock (`FOR UPDATE`), never read-then-write.
- Time comes from `TimeProvider`; nothing reads `DateTime.UtcNow`.

## Endpoint index
| Feature | Owner / creator (`api.zaa.gl`) | Link holder (`api.zaa.gl`, gated) |
|---|---|---|
| Vault | `POST/GET /api/vault/secrets`, `GET/DELETE /api/vault/secrets/{id}` | `GET /api/public/vault/{code}`, `POST .../reveal` |
| Webhook catcher | `POST/GET /api/hooks`, `GET/PATCH/DELETE /api/hooks/{id}`, `GET/DELETE /api/hooks/{id}/requests`, `GET/DELETE /api/hooks/{id}/requests/{rid}`, `GET /api/hooks/{id}/stream` (SSE) | ingest `ANY zaa.gl/hook/{token}[/…]` (short host) |
| Before/after | `POST /api/compare`, `GET/PATCH/DELETE /api/compare/{id}` | `GET /api/public/compare/{code}` |
| Viewers | (normal uploads) | `GET /api/public/content/{code}` gains `file.viewer` + `inlineUrl` |
| Palette | (computed) | `media.palette` in owner and public descriptors |
| Pinned comments | `PUT /api/resources/{id}/comments/settings`, `GET /api/resources/{id}/comments`, `POST .../{cid}/resolve`, `.../reopen`, `DELETE .../{cid}` | `GET/POST /api/public/comments/{code}`, `DELETE /api/public/comments/{code}/{cid}` |
| Bug capsules | `POST /api/capsules`, `GET/DELETE /api/capsules/{id}` | `GET /api/public/capsules/{code}` |
| Approval gate | `POST /api/approvals`, `GET/DELETE /api/approvals/{id}`, `POST /api/approvals/{id}/release` | `GET /api/public/approvals/{code}`, `POST .../approve`, `POST .../download` |
Owner library for every tool link: `GET /api/resources?kind=secret|capsule|compare|approval`; title, renewal, public and password
through `PATCH /api/resources/{id}` (vault: never public, no renewal once closed).

New configuration (all optional, defaults in code): `Plans:<Guest|Free|Starter>:Tools:*`, `Hooks:*` (`ReadOnlyGraceHours`,
`StreamMaxMinutes`, `MaxStreamsPerUser`, `IngestBaseUrl`), `Comments:*` (`MaxPerResource`, `MaxBodyLength`), `Viewers:MaxJsonBytes`,
`RateLimits:HookIngestPerMinute`, `RateLimits:CommentWritePerMinute`. New CAPTCHA actions: `guest-vault`, `guest-comment`,
`guest-approve`. New API scope: `tools:write`. New error codes: `secret-too-large`, `logs-too-large` (413), `limit-reached` (409),
`resource-unusable` (422).

## Migrations (expand-only, one set per owning module)
- Links: `WidenTargetKindForPhase2Tools` (NOT VALID) + `ValidatePhase2TargetKind`.
- Resources: `WidenResourceKindForPhase2Tools` (NOT VALID) + `ValidatePhase2ResourceKind`.
- Files: `AddViewerKind` + `ValidateViewerKind`, `AddMediaPalette`, `WidenViewerKindForVideo` + `ValidateViewerKindForVideo`.
- DeveloperTools (new schema `devtools`): `Vault`, `WebhookCatcher`, `BugCapsules`.
- Collaboration (new schema `collab`): `Comparisons`, `PinnedComments`, `ApprovalGate`.
Every VALIDATE runs in its own migration outside a transaction (SHARE UPDATE EXCLUSIVE only), like `ValidateLinkTargetKindChecks`.

## Known gaps (not built in this slice)
1. **Account deletion and export (spec §7.14)**: tool links follow the Phase 1 path (links soft-deleted → C10 → wipe/purge), but
   webhook catchers are not links and stay until their own purge (at most lifetime + 24 h, ≤ 8 days); comments written on other
   people's content keep `author_user_id`; the data export does not list secrets' metadata, catchers or comments. Identity calls
   only the Links eraser today (`src/Modules/Identity/Application/IdentityService.cs:689`).
2. **Admin actions** (`/api/admin/*`) for catchers and comments: none; takedown of tool links works through the normal link takedown.
3. **Idempotency-Key** is not supported on the Phase 2 create routes (retries create a second item; guest retries use another slot).
4. **Palette backfill** for images processed before this release.
5. Observation outside this scope: the scan task overwrites the type detected at `/complete` with the image/viewer sniff only
   (`src/Modules/Files/Application/ScanTaskHandler.cs:139` with `:252`), so PDF/ZIP/GZ/7z files end with `detected_mime = null`
   (served as octet-stream attachments, which is safe) even though `/complete` detected them
   (`src/Modules/Files/Application/UploadService.cs:425`). Left unchanged to keep Phase 1 behaviour; worth a Phase 1 ticket.

## Open questions for the owner (all features)
Collected in each note; the cross-cutting ones:
1. Approve or change the plan table above (the PRD's Pro tier does not exist in Phase 1).
2. Guests and the vault: allowed as proposed (one view, 7 days, 16 KB, CAPTCHA), or accounts only?
3. Do Phase 2 tool creations count toward the guest PD-14 budget (proposed: yes, one shared budget) and toward Free's monthly
   link cap (PD-08 says no for content; proposed the same for tools)?
4. Statistics for tool links: none recorded until PD-10 is decided (same as Phase 1 content).
