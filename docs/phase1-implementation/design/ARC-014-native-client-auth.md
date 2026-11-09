# ARC-014: CLI and desktop sign-in (device authorization) and scope enforcement

| | |
|---|---|
| Owner | zaagl-architect |
| Status | DESIGN for BE-024 (device flow), BE-001 follow-up (scopes), CLI-011, desktop sign-in, FE `/activate`. Stage 2 work (ON HOLD per PD-50/PD-51 until the lead asks the product owner). |
| Decisions | PD-07 (APPROVED), PD-19, PD-24 (partial), PD-36, PD-40, PD-41 (APPROVED). ADR-0007, ADR-0013, ADR-0017, ADR-0018. |
| Rulebook | Addendum §14, §19, §18, §16 MUST NOT 17, 21, 23. |
| Contract | C11, `src/Modules/Identity/Contracts/NativeClientContracts.cs` (`TokenScopes`, `NativeClientIds`, `NativeClientClaims`, `DeviceFlowWire`, `DeviceCodeRequest/Response`, `DeviceTokenRequest/Error`, `DeviceActivationInfo/Request`, `DeviceSessionDto`). Names below are exactly those. |
| Line numbers | Working tree of 2026-11-09; other agents are editing Identity and Api files, so locate code by the method or symbol named. |

## 1. What exists today

| Fact | Evidence |
|---|---|
| Default auth scheme "Smart": `X-Api-Key` header → ApiKey scheme; `/hubs/*?ticket=` → hub-ticket scheme; otherwise JWT bearer. | `src/Zaagl.Api/Program.cs:98-106` |
| API key handler: claims `sub`, `auth_method=api_key`, one `scope` claim per key scope. **No plan check**: a key keeps working after a downgrade. | `src/Zaagl.Api/Infrastructure/ApiKeyAuthentication.cs:22-35`; `IdentityService.ValidateApiKeyAsync` (`IdentityService.cs:603-613`) checks only hash and `RevokedAt` |
| API key creation requires `Tier == Starter` (BE-003 replaces with the `ApiKeys` capability, ARC-005). | `IdentityService.cs:442` |
| Allowed key scopes include the Phase 1 content scopes; default set is the Phase 0 three. | `IdentityService.cs:41-46` |
| Session JWT: `sub`, `email`, `name`, `sstamp`, three `scope` claims, plus `role` when set. | `src/Modules/Identity/Application/TokenService.cs:52-62` |
| Scope policies: a JWT passes every scope; an API key must hold the `scope` claim. `RequireScopeWhenAuthenticated` for guest-capable routes. `403 insufficient-scope`. | `src/Zaagl.Api/Infrastructure/ApiScopes.cs:31-38, 63-71, 91-104` |
| "Interactive" policy = JWT bearer scheme + authenticated. It does **not** distinguish a device token (none exist yet). | `Program.cs:194-196` |
| "Admin" policy = JWT + role Admin + `AdminNetworkRequirement`. | `Program.cs:200-204` |
| Refresh: rotation with family id, reuse detection revokes the family, `refresh_raced` within the benign window. The refreshed access token is issued from the user row only, so it cannot know which client the family belongs to. | `IdentityService.cs:225-239` (`IssueSessionAsync`), `:246-280` (`RefreshAsync`); `RefreshToken` entity `src/Modules/Identity/Domain/User.cs:89-99` (no client columns) |
| One-time codes (PD-41): 32 random bytes, SHA-256 stored, conditional DELETE consume, 60 s. Pattern to reuse for device codes. | `src/Modules/Identity/Application/OneTimeCodes.cs:15-60` |
| Auth routes live under `/api/auth`; `/api/api-keys`, `/api/me`, `/api/billing/*`, `/auth/me`, `/auth/hub-ticket`, `/auth/logout-everywhere` require Interactive. | `src/Zaagl.Api/Endpoints/IdentityEndpoints.cs:30-245`, `BillingEndpoints.cs:15-30` |

## 2. Device authorization flow (RFC 8628)

```
CLI/Desktop                     api.zaa.gl                          app.zaa.gl (/activate, BFF)        User
  | POST /api/auth/device/code  |                                          |                              |
  |  {client_id, device_name, os}                                          |                              |
  |---------------------------->| store device_code_hash, user_code_hash,  |                              |
  |                             | client_id, name, os, expires, interval   |                              |
  |<----------------------------| {device_code, user_code, verification_uri, verification_uri_complete,     |
  |                             |  expires_in: 600, interval: 5}           |                              |
  | show "Go to app.zaa.gl/activate, code BCDF-GHJK" and open browser      |                              |
  |                             |                                          |<---- open /activate?code=... |
  |                             |                                          | sign in if needed (BFF)      |
  |                             |<-- GET /api/auth/device/activation?userCode=   (session JWT, via BFF)   |
  |                             |--> DeviceActivationInfo (client, device, os, requested, expires)        |
  |                             |                                          | shows details, Approve/Deny  |
  |                             |<-- POST /api/auth/device/activate {userCode, approve} (session JWT, CSRF)|
  | POST /api/auth/device/token (every interval)                           |                              |
  |  {grant_type, device_code, client_id}                                  |                              |
  |<-- 400 authorization_pending / slow_down / access_denied / expired_token, or 200 AuthResponse + sid    |
```

### 2.1 `POST /api/auth/device/code`
- Anonymous; rate limit `device-code` per IP partition (proposed 10/min, 60/h; configuration `RateLimits:DeviceCode*`).
- `client_id` must be one of `NativeClientIds` (`zaagl-cli`, `zaagl-desktop`), else `400 invalid_client`.
  `device_name` (≤ 64 chars) and `os` (≤ 32 chars) are sanitised display data (control and bidi characters stripped).
- `device_code`: 32 CSPRNG bytes, base64url; stored only as SHA-256 (same as `OneTimeCodes.cs:27,33`).
- `user_code`: 8 characters from `DeviceFlowWire.UserCodeAlphabet` (20 consonants, no vowels so no words, no look-alikes),
  shown `XXXX-XXXX`; ~34.6 bits. Stored as SHA-256 of the normalised value (upper-case, hyphen and spaces removed).
  Unique among unexpired rows (retry on collision).
- `expires_in` = `DeviceAuth:ExpiresInSeconds` (default 600), `interval` = `DeviceAuth:IntervalSeconds` (default 5).
- `verification_uri` = configuration `DeviceAuth:VerificationUri` (`DeviceFlowWire.VerificationUriConfigKey`; production
  default `DeviceFlowWire.DefaultVerificationUri` = `https://app.zaa.gl/activate`; staging sets its own host), never
  built from a request header. `verification_uri_complete` = that value + `?code=XXXX-XXXX`. The user code in a query string is
  acceptable (RFC 8628 §3.3.1); `/activate` uses `Referrer-Policy: no-referrer` and removes the query with
  `history.replaceState`.
- Table `identity.device_authorizations(id uuid, device_code_hash bytea unique, user_code_hash bytea, client_id,
  device_name, os, status smallint (pending, approved, denied, redeemed), user_id null, created_at, expires_at,
  last_polled_at, poll_interval_seconds, failed_entries int)`. Expired rows swept like `OneTimeCodes.cs:30`.

### 2.2 `GET /api/auth/device/activation?userCode=` and `POST /api/auth/device/activate`
- Called only by the BFF on behalf of `app.zaa.gl/activate`, with the **session JWT** (policy Interactive after §4.2's
  change, so a device token cannot approve another device). The POST goes through the BFF's CSRF check
  (`__Host-zaagl_csrf` + header, addendum §4.2.4).
- Unknown, expired, already used or denied code → `404` (no distinction). Lookup is by `user_code_hash`.
- **Brute force:** rate limit `device-activate` per user (proposed 10/min) and per IP partition (20/min); a wrong code
  increments a per-user failure counter, 10 failures in 10 minutes → `429` for 15 minutes (all four numbers are
  technical defaults in configuration, `DeviceAuth:Lockout*`, not product rules). Because the code space is
  ~2^34.6 and a code lives 10 minutes, online guessing is negligible under these limits.
- **Phishing mitigation:** the page shows client (CLI or desktop app), device name, OS, request time and "only approve
  if you started this sign-in yourself in the last 10 minutes"; approval is an explicit button, never automatic on
  page load, even with `verification_uri_complete`.
- Approve: one conditional `UPDATE ... SET status = approved, user_id = :uid WHERE id = :id AND status = pending AND
  expires_at > now` (exactly one winner). Deny: `status = denied`.
- Admin accounts may approve; the resulting tokens still carry no Admin role (§2.4).
- Guests cannot (no session). Disabled users cannot (existing disabled-account check of login applies).

### 2.3 `POST /api/auth/device/token`
- Anonymous; JSON or form body; rate limit `device-token` per IP partition (proposed 30/min).
- `grant_type` must equal `DeviceFlowWire.GrantType`, else `400 unsupported_grant_type`; unknown or mismatched
  `client_id` → `400 invalid_client`; unknown device code → `400 invalid_grant`.
- Polling faster than `poll_interval_seconds` → `400 slow_down` and the stored interval grows by
  `SlowDownIncrementSeconds` (RFC 8628 §3.5).
- `pending` → `authorization_pending`; `denied` → `access_denied` (row then deleted); expired → `expired_token`.
- `approved` → conditional `UPDATE status = redeemed WHERE status = approved` (single use; a second poll after
  redemption gets `invalid_grant`), then issue the session (§2.4) and return `200` with the existing `AuthResponse`
  (`IdentityContracts.cs:8`) plus `sessionId`.
- Errors use the RFC 6749 body `DeviceTokenError { error, error_description }` (not RFC 7807): the token endpoint is
  OAuth-shaped so standard device-flow client libraries work. All other routes keep RFC 7807 (BE-002).

### 2.4 Tokens and device sessions
- Access JWT claims: everything in `TokenService.cs:52-62` **except** the role claim, plus
  `client_id` (`NativeClientClaims.ClientId`), `amr=device` (`NativeClientClaims.Amr`/`AmrDevice`) and
  `sid` (the refresh family id, `NativeClientClaims.SessionId`). Scope claims: all six `TokenScopes` (JWT and device
  sessions hold all non-admin scopes, addendum §14.4). The `sstamp` claim keeps the security-stamp revocation of
  spec §7.4 working unchanged.
- **Never the Admin role**, even for an admin user (MUST NOT 17): `IssueAccessToken` gains a "native" variant that
  omits `ClaimTypes.Role`. The Admin policy (`Program.cs:200-204`) additionally refuses `amr=device`.
- Refresh family: `RefreshToken` (`User.cs:89-99`) gains family-level fields, best stored on a new
  `identity.device_sessions(family_id pk, user_id, client_id, device_name, os, created_at, last_used_at, revoked_at)`
  row, so `RefreshAsync` (`IdentityService.cs:246`) re-issues a **device** access token for a device family and a web
  token for a web family. Without this, a refreshed device token would silently become a web token (gap to fix in
  BE-024). Rotation, reuse detection and `refresh_raced` stay exactly as `IdentityService.cs:246-280`.
- Lifetimes: access token as today; device refresh family lifetime and an absolute cap are **OPEN (PD-33, "native
  session lifetime cap")**; until decided, the existing 30-day sliding refresh (`IdentityService.cs:229`) applies.
- Session list and revoke (routes PROPOSED, part of BE-024): `GET /api/me/sessions` → `DeviceSessionDto[]`,
  `DELETE /api/me/sessions/{sessionId}` → revokes the family (and its access tokens within the security-stamp cache
  bound by bumping a per-family revocation check, or accepting access-token expiry as the bound; BE to choose and
  record). Both Interactive (web session only). `POST /api/auth/logout-everywhere` (`IdentityEndpoints.cs:101-106`)
  also revokes device families.

## 3. Policies

### 3.1 Interactive excludes native tokens
Change `Program.cs:194-196` to `RequireAssertion(ctx => !ctx.User.HasClaim(NativeClientClaims.Amr,
NativeClientClaims.AmrDevice))` in addition to the JWT scheme. Effect: API-key management, `/api/me` (delete, export),
`/api/auth/me`, billing, `logout-everywhere`, `hub-ticket`, TOTP and device activation refuse device tokens with `403`.
Open detail: the CLI may want `whoami`; give it a non-Interactive `GET /api/auth/whoami` returning `UserDto` (BE-024)
instead of loosening `/api/auth/me`.

### 3.2 API keys
- Creation gate: `ApiKeys` capability (ARC-005, BE-003), not the tier (`IdentityService.cs:442`).
- **Gap (spec §7.4, addendum §14.3, MUST NOT 17):** keys must stop working when the owner loses `ApiKeys`.
  `ValidateApiKeyAsync` (`IdentityService.cs:603-613`) and the handler (`ApiKeyAuthentication.cs:27-29`) do not check
  the plan. Fix in BE-003/BE-024: the handler asks `IContentPlanProvider` (or `IPlanProvider` capabilities) and
  returns `AuthenticateResult.Fail` → `401` with `WWW-Authenticate` (PD-36, never guest). Cache the plan per user for at
  most 60 s, the same bound as the security-stamp cache.
- Scope strings: `ApiScopes` (`ApiScopes.cs:15-22`) and `IdentityService.AllowedScopes` (`IdentityService.cs:41-42`)
  duplicate the wire strings; both should reference `TokenScopes` (contract owner: Identity).
- `TokenService.cs:60` issues only three scope claims; harmless today because `ApiScopes.Allows` passes any JWT
  (`ApiScopes.cs:34-35`), but BE-024 should issue all six so the claims are truthful.

### 3.3 Failing credentials (PD-36, addendum §19)
Any credential present and failing (bad/expired JWT, wrong/revoked key, key of a plan without `ApiKeys`, enrol-only
token, device token of a revoked family) → `401` + `WWW-Authenticate` on every route, including guest-capable ones,
before CAPTCHA or any counter. The "Smart" selector (`Program.cs:103-106`) already routes a present `X-Api-Key` to
the key scheme; BE-024 adds the guest-capable content routes to the PD-36 test set.

## 4. Scope enforcement matrix

Credential columns: **W** web session JWT via the BFF; **D** device JWT (`amr=device`); **K(s)** API key holding
scope s (paid plans only); **G** no credential (guest); **A** admin JWT with TOTP from an allowed network.
Results: `ok`; `401` failing credential; `403s` = `403 insufficient-scope`; `403i` = refused by Interactive;
`404o` = not the owner (admin included, PD-40); `-` = route does not accept that caller (`401` when anonymous).

| Route group (evidence) | Scope | W | D | K | G | A on another user's data |
|---|---|---|---|---|---|---|
| `POST /api/links` (`LinksEndpoints.cs:36-82`) | links:write | ok | ok | K(links:write) else 403s | guest path (CAPTCHA) | n/a (creates own) |
| `GET /api/links`, `/{id}`, `/slug-available`, `/api/tags`, `GET /api/folders` (`LinksEndpoints.cs:26-34, 90-92, 107-109, 123-131`) | links:read | ok | ok | K(links:read) | - | 404o |
| `PUT/PATCH/DELETE /api/links/{id}`, `/renew`, `/bulk`, folder writes (`LinksEndpoints.cs:85-105, 111-120, 133-144`) | links:write | ok | ok | K(links:write) | - | 404o |
| Analytics `/api/links/{id}/analytics*`, `/api/analytics/overview`, `/api/archive*` (`AnalyticsEndpoints.cs:15-123`) | stats:read | ok | ok | K(stats:read) | - | 404o |
| `GET /api/public/stats/{slug}` (`AnalyticsEndpoints.cs:92-101`) | none | ok | ok | ok | ok | n/a |
| `POST /api/unlock/{slug}` (`ResolutionEndpoints.cs:73-79`) | none | ok | ok | ok | ok | n/a |
| `POST /api/reports` (`AdministrationEndpoints.cs:204-214`) | none + CAPTCHA | ok | ok | ok | ok | n/a |
| `/api/api-keys*`, `/api/me*`, `/api/auth/me`, `logout-everywhere`, `hub-ticket`, `totp/*` (`IdentityEndpoints.cs:101-124, 183-201, 238-245`) | Interactive | ok | 403i | - (JWT scheme only) | - | own only |
| `/api/billing/me`, `/checkout`, `/portal` (`BillingEndpoints.cs:15-30`) | Interactive | ok | 403i | - | - | own only |
| `/api/admin/*` (`AdministrationEndpoints.cs:24-184`) | Admin | role-gated | 403 (never Admin) | - | - | ok (only path for admins, PD-40) |
| `/hubs/analytics?ticket=` (`Program.cs:277`) | hub ticket | ticket via BFF | - | - | - | own links only |
| `POST /api/auth/device/code`, `/device/token` (new) | none | ok | ok | ok | ok | n/a |
| `GET /api/auth/device/activation`, `POST /device/activate` (new) | Interactive | ok | 403i | - | - | own only |
| `GET/DELETE /api/me/sessions*` (new, PROPOSED) | Interactive | ok | 403i | - | - | own only |
| `POST /api/snippets`, `/api/snippets/raw` (C9) | snippets:write | ok | ok | K(snippets:write) | guest path (snippets only, not `/raw`) | n/a |
| `GET/PUT/DELETE /api/snippets/{id}*`, versions, diff (C9) | resources:read (GET), snippets:write (PUT/DELETE) | ok | ok | K(scope) | - | 404o |
| `POST /api/uploads`, tus `HEAD/PATCH/DELETE`, `/complete`, `/uploads/simple` (C8, C9) | files:write | ok | ok | K(files:write) | guest path (create, simple) | 404 (session not theirs) |
| `GET /api/resources*`, `/status`, `GET /api/me/storage` (C9) | resources:read | ok | ok | K(resources:read) | - | 404o |
| `PATCH/DELETE /api/resources/{id}` (C9) | kind write scope: snippets:write (snippet) or files:write (file, media); `resources:write` OPEN (Q3) | ok | ok | K(kind scope) | - | 404o |
| `POST /api/resources/{id}/download` (C9) | resources:read | ok | ok | K(resources:read) | - | 404o |
| `GET /api/public/content/{code}`, `/unlock`, `/download` (C9) | none (gate: key, grant) | ok | ok | ok | ok | n/a |

Notes: `GET /api/me/storage` lives under `/api/me` whose group is Interactive today (`IdentityEndpoints.cs:238`); the
CLI needs it with a device token or key, so it is mapped outside that group (or the group's policy is set per route).
Phase 1 routes not yet built are bound by this matrix when they ship (BE-001 note in `ApiScopes.cs:19`).

## 5. Client token storage

- Desktop (Tauri 2): refresh token in the OS keychain through the `keyring` plugin (Windows Credential Manager, macOS
  Keychain); access token in memory only. Desktop shortcut default Ctrl+Alt+Z / Cmd+Ctrl+Z (PD-19).
- CLI (.NET AOT): OS keychain where available (Windows Credential Manager, macOS Keychain, libsecret on Linux
  desktops). Headless Linux credentials (environment variable or file with 0600) are **OPEN (PD-33)**; until decided
  the CLI refuses to store tokens in plain files and supports `ZAAGL_API_KEY` for paid automation only.
- Both: `zaagl logout` calls `/api/auth/logout` with the refresh token and deletes the keychain entry. Link keys
  (`#k=`) are never stored by the clients beyond printing or copying the share URL.

## 6. Deferred (PD-24 partial, PD-51)

Test isolation QA-001..003 is deferred; device-flow and scope tests (QA-010 matrix, PD-36 negatives) are testing tasks
left for later under PD-51 and are listed here only as acceptance criteria.

## 7. Questions for the user

1. **Session lifetime for the CLI and desktop app** (PD-33): how long may a device stay signed in without use?
   Options: 30 days sliding like the web (today), 90 days sliding, or 30 days sliding with a 1-year absolute cap.
   Recommendation: 90 days sliding with a 1-year cap; the user can revoke any device in settings.
2. **Headless servers** (PD-33, OPEN). Two proposals differ and the user should see both: (a) environment variable
   only, as the PD-33 entry in DECISIONS.md proposes (no secret on disk; Free users must export a token each session);
   (b) a file readable only by the user (0600) when no keychain exists, plus the environment variable for CI.
   Architect recommendation: (b), so Free users can use the CLI over SSH. Until decided, the CLI stores no token in a
   plain file.
3. **Which scope covers managing content** (renew, delete)? Interim rule used by contracts.md C9, §4 and the OpenAPI
   draft: the kind's write scope (`snippets:write` or `files:write`); owner download is a read (`resources:read`).
   Option: add `resources:write` (a contract change to `TokenScopes`). Recommendation: keep the interim rule unless
   the user wants one scope for all content management.
4. **Device session list and revoke routes** are proposed here (`/api/me/sessions`); confirm they are in Stage 2 scope.
   Recommendation: yes, PD-07 ("listed and revocable") requires them.

## 8. Review checklist

BE-001 (scopes, done in Stage 1; follow-ups)
- [ ] `ApiScopes` and `IdentityService.AllowedScopes` reference `TokenScopes`; no other literal scope strings.
- [ ] Every endpoint in §4 has exactly one scope or policy; new Phase 1 routes added to the QA-010 matrix.
- [ ] Session JWT issues all six scope claims.

BE-024 (device flow)
- [ ] Routes, bodies and error codes exactly as §2 and the C11 stub; RFC 6749 error body on `/device/token` only.
- [ ] Device and user codes stored as hashes; approve and redeem are conditional updates with one winner.
- [ ] `slow_down` grows the interval; expired and denied codes cannot be redeemed; second redeem → `invalid_grant`.
- [ ] Device tokens: `client_id`, `amr=device`, `sid`, no role claim, `sstamp` kept; refresh keeps them (device
      session row).
- [ ] Interactive and Admin policies refuse `amr=device`.
- [ ] API keys fail with `401` after losing `ApiKeys` (within 60 s); never fall back to guest (PD-36).
- [ ] Rate limits `device-code`, `device-activate`, `device-token` registered; failure lockout on activation.
- [ ] Device sessions listed and revocable; `logout-everywhere` revokes them.

CLI-011 / desktop
- [ ] Uses `verification_uri_complete`, prints the user code, honours `interval` and `slow_down`.
- [ ] Refresh token only in the OS keychain; no plain-file storage until PD-33 is decided.
- [ ] Parses `#k=` from share URLs locally and sends it only in `X-Zaagl-Link-Key`.
- [ ] Handles `401` by refreshing once, then asking the user to sign in again; handles `403 insufficient-scope`.
