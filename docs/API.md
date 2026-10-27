# API reference

A human-readable map of what exists. The generated OpenAPI document (`/openapi/v1.json`, browsable via
Scalar in Development) is the authoritative, field-level contract; this page is for finding the right
endpoint quickly. Routes are this app's actual `/api/*` paths, not the spec's literal `/api/v1/*` (a
documented deviation, see `docs/DECISIONS.md`).

## Auth (`/api/auth`)

| Route | Notes |
|---|---|
| `POST /register` | Always `202`, same response regardless of whether the address exists (spec §7.4) |
| `POST /resend-confirmation` | Always `202` |
| `GET /account/confirm-email`, `POST /confirm-email` | Fragment-token page + the endpoint it posts to |
| `POST /forgot-password` | Always `202` |
| `GET /account/reset-password`, `POST /reset-password` | Same fragment-token pattern |
| `POST /login` | `{ email, password, totpCode? }` -> `{ accessToken, refreshToken, ... }`; Admin without an enrolled authenticator gets `{ enrolToken }` instead |
| `POST /refresh` | Rotates the refresh token; `409 refresh_raced` on a benign concurrent double-submit, `401` on genuine reuse (revokes the whole family) |
| `POST /totp/enroll`, `/totp/confirm` | Admin-only TOTP setup |
| `POST /logout`, `/logout-everywhere` | |
| `GET/POST/DELETE /api/api-keys` | Starter plan only; up to 20 keys |

## Links (`/api/links`)

| Route | Notes |
|---|---|
| `POST /api/links` | No credentials -> a guest link (§7.12, `Captcha-Token` required, `429 quota`/`guest_creation_paused` on limits); authenticated -> an owned link. `Idempotency-Key` header supported |
| `GET /api/links`, `GET /{id}` | Owner-only (guest links are never returned here) |
| `PUT /{id}`, `PATCH /{id}` | Update destination/policy/rules, or just flags (`isActive`, `publicStats`) |
| `POST /{id}/renew` | Forward-only; `400 renewal-must-extend` otherwise |
| `DELETE /{id}` | Soft delete; alias held until `purgeAfter` |
| `GET /slug-available` | Pre-flight custom-slug check |
| `POST /bulk` | CSV import, up to 1 MB |
| `GET /api/tags` | |
| `GET/POST/PUT/DELETE /api/folders` | |

## Account (`/api/me`)

| Route | Notes |
|---|---|
| `DELETE /api/me { password }` | Irreversible; last remaining admin cannot delete itself (§7.14) |
| `GET /api/me/export` | Profile + links + archived links as JSON (no CSV yet, see `docs/TRACEABILITY.md`) |

## Reports (public)

| Route | Notes |
|---|---|
| `POST /api/reports { code?, url?, category, details?, reporterEmail? }` | No account needed |

## Admin (`/api/admin/*`, all require the `Admin` policy: role + TOTP-backed session + `Admin:AllowedNetworks`)

| Route | Notes |
|---|---|
| `GET /users`, `POST /users/{id}/disable`, `/enable` | `disableLinks`/`restoreLinks` cascade to the user's links |
| `GET /links/{code}`, `POST /disable`, `/enable`, `/reinstate` | Admin link view and moderation |
| `POST /takedowns` | Bulk, filtered by host/guest-creator-key/owner/date; `dryRun` first |
| `GET /reports`, `POST /reports/{id}/resolve` | |
| `GET/POST/DELETE /blocked-destinations`, `/blocked-aliases`, `/alias-denylist` | Standing lists |
| `POST /declare-restore`, `GET /alias-freezes`, `POST /alias-freezes/{id}/end` | §7.11 |
| `GET /allocator` | Proxies the coordinator's `/v1/status` |

See `docs/ABUSE.md` for how these fit into the actual moderation workflow.

## Analytics, archive, billing, access, organizations

Built in earlier phases (Phase 0 scaffold through Phase 4 of this rebuild); see their own endpoint files
(`AnalyticsEndpoints.cs`, `ArchiveEndpoints` under `MapArchive`, `BillingEndpoints`, `AccessEndpoints`) and
the generated OpenAPI document for their exact shapes - not re-described here since they're unchanged by
this phase's work.

## Webhooks

| Route | Notes |
|---|---|
| `POST /api/webhooks/email` | HMAC-SHA256 signed (`X-Signature` header, hex); bounce/complaint events suppress the address |
| `POST /api/billing/webhook` | Stripe webhook (Phase 0, unchanged) |

## Health (internal network only)

`GET /health/live`, `/health/ready`, `/health/issuer` - see `docs/RUNBOOK.md`.
