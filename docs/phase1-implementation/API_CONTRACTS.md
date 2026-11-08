# Phase 1 API contracts

| | |
|---|---|
| Owner | zaagl-backend (contracts), reviewed by zaagl-architect (ARC-016) |
| Status | PLANNING, awaiting approval. No Phase 1 code exists yet. |
| Source | _drafts/backend.md §3, _drafts/desktop-cli.md §3, _drafts/frontend.md §5, written 2026-11-09 by the planning agent named above and kept verbatim below this header |
| Decisions | Numbered PD-nn in [DECISIONS.md](DECISIONS.md). Draft question ids (D-n, Qn, Q-nn) map to PD entries there. |

## Consolidation notes

These are the lead architect's overrides. Where they differ from the draft text below, they win.

- **Status:** proposals. The OpenAPI document produced by ARC-016 and BE-028 becomes the source of truth; this file is its input.
- **Prefix:** Phase 0 routes are `/api/...`, while spec §7.9 says `/api/v1/...`. New routes follow the versioning ARC-016 decides (PD-33). Routes below are written as `/api/...`.
- **Viewer hosts (PD-02):** Part C assumes viewers may live on `zaa.gl`; under the consolidated position they call these APIs from `app.zaa.gl`.
- **Upload host (PD-12):** open. The tus endpoints exist on whichever host is chosen.
- **Burn (PD-05):** the backend draft's definition (DATABASE_AND_STORAGE.md Part B) is the proposal.
- **CLI and desktop auth (PD-07):** OAuth device authorization; endpoints in Part B.
- **Before any endpoint ships:** an entry in the generated authorization matrix (QA-010), a scope (BE-001), a rate-limit policy, an RFC 7807 problem `type` (BE-002), and idempotency where marked.

---

## Part A: backend content API (backend draft)

All JSON camelCase, UTC ISO 8601; errors RFC 7807 `{type:"https://zaa.gl/problems/<name>", title, status, code}`. Auth column: **J** = JWT, **K(scope)** = API key with scope, **G** = guest (no credentials + `Captcha-Token`), **P** = public (gates evaluated), **T** = signed ticket. Every write tx: `SET LOCAL lock_timeout='3s'`, SQLSTATE 55P03 → `503 busy` + `Retry-After: 1` (spec §7.0).

### 3.1 Snippets
| Method & route | Auth | Request → Success | Errors |
|---|---|---|---|
| `POST /api/snippets` | J, K(snippets:write), G? | `{content, language?, renderMode?, title?, expiresAt?, burnAfterView?(Q11), password?, customSlug?}` + `Idempotency-Key?` → `201 {id, publicId, code?, shortUrl?, version:1, sizeBytes, lineCount, expiresAt, state}` | `400 validation`, `413 snippet-too-large`, `402 plan-required`, `409 alias-unavailable/alias-already-yours/alias-frozen`, `422 idempotency-mismatch`, `429 quota`, `503 busy` |
| `POST /api/snippets/raw` (CLI pipe) | K(snippets:write), J | body `text/plain; charset=utf-8` streamed, query `?lang=&title=&ansi=keep|strip&expires=7d&burn=false` → `201` same body; `Accept: text/plain` → just the short URL + `\n` | `413 snippet-too-large` (enforced while reading, not after), `415 unsupported-media-type` (non-UTF-8) |
| `GET /api/snippets?cursor&limit&state` | J, K(resources:read) | `{items:[{id, code, title, language, sizeBytes, version, state, expiresAt, createdAt}], nextCursor}` | — |
| `GET /api/snippets/{id}` | owner | `{…, content, renderedHtml? (markdown only)}` | `404` |
| `PUT /api/snippets/{id}` | owner, K(snippets:write) | `{content, language?, renderMode?, baseVersion}` → `200 {version}` | `409 version-conflict` (baseVersion ≠ current), `409 not-active`, `413`, `422 version-limit` (100) |
| `GET /api/snippets/{id}/versions` / `/{n}` | owner | list / one version | `404` |
| `GET /api/snippets/{id}/diff?from=&to=` | owner or P (for public snippets, Q10) | `200 {from,to, lines:[{left:{no,text,type}, right:{no,text,type}}], stats:{added,removed,changed}}` (DiffPlex `SideBySideDiffBuilder`) | `413 diff-too-large` (> 1 MB or 20k lines combined) |
| `DELETE /api/snippets/{id}` | owner | `204` (soft delete; link 410) | `404` |
| `GET /api/public/snippets/{code|publicId}` | P (+ `X-Viewer-Token` if locked) | `{title, language, renderMode, version, versions:[n], content, renderedHtml?, sizeBytes, lineCount, expiresAt, owner:{displayName?}}`, `Cache-Control: private, no-store` | `401 password-required`, `404`, `410 expired/burned/disabled` |
| `GET https://<content-host>/s/{publicId}/raw[/v/{n}]` | P/T | `text/plain; charset=utf-8`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox; default-src 'none'`, `Content-Disposition: inline` | as above |
Markdown: Markdig (`DisableHtml()`, no raw HTML, autolinks `rel=nofollow noopener`), then HtmlSanitizer allowlist; cached per `(version id)` in HybridCache with a byte size (MUST NOT #24). Syntax highlighting is client-side; server only stores `language`.

### 3.2 Uploads (tus 1.0 via tusdotnet; served on the upload/content host for >100 MB, also on API host ≤ 95 MB chunks)
| Method & route | Auth | Request → Success | Errors |
|---|---|---|---|
| `OPTIONS /api/uploads` | — | `Tus-Version: 1.0.0`, `Tus-Extension: creation,creation-with-upload?,termination,expiration,checksum`, `Tus-Max-Size: <plan max>` | — |
| `POST /api/uploads` (create) | J, K(files:write) | headers `Upload-Length`, `Upload-Metadata: filename <b64>,filetype <b64>,kind <b64 file|media>`, `Idempotency-Key?` → `201 Location: /api/uploads/{id}`, `Upload-Expires` | `413 file-too-large` (vs plan, before any byte), `402 plan-required`, `403 quota-exceeded` (reservation fails), `400 upload-length-required` (no `Upload-Defer-Length`), `429 rate-limit` |
| `HEAD /api/uploads/{id}` | same principal | `200 Upload-Offset, Upload-Length, Cache-Control: no-store` | `404`, `410 upload-expired` |
| `PATCH /api/uploads/{id}` | same principal | `Content-Type: application/offset+octet-stream`, `Upload-Offset`, optional `Upload-Checksum: sha1 …` → `204 Upload-Offset` | `409 upload-offset-mismatch`, `460 checksum-mismatch` (tus), `413 chunk-too-large` (> `Uploads:MaxChunkBytes`, default 64 MiB), `410 upload-expired` |
| `DELETE /api/uploads/{id}` | same principal | `204` (temp deleted, reservation released) | `404` |
| `POST /api/uploads/{id}/complete` | same principal | `{title?, expiresAt?, burnAfterDownload?, password?, customSlug?}` + `Idempotency-Key` → `202 {resourceId, publicId, code?, shortUrl?, state:"processing"}` | `409 upload-incomplete`, `409 already-finalized` (replays body if same key), `413`, `422 idempotency-mismatch` |
| `POST /api/uploads/simple` (desktop screenshots, CLI ≤ 25 MB) | J, K(files:write) | raw body `application/octet-stream`, query `?filename=&kind=&expires=&burn=` → `202` as above | `411 length-required`, `413` |
Ownership: the upload id is a capability **plus** principal check (session `owner_id` must equal caller). tusdotnet `OnAuthorizeAsync` does the check; `OnBeforeCreateAsync` does plan/size/quota; `OnFileCompleteAsync` only marks the session complete (finalize is explicit so metadata can be set after bytes).

### 3.3 Resources, media, delivery
| Method & route | Auth | Request → Success | Errors |
|---|---|---|---|
| `GET /api/resources?kind&state&cursor&limit` | J, K(resources:read) | `{items:[{id, kind, title, code, shortUrl, sizeBytes, state, processingStatus, expiresAt, burnAfterDownload, downloadCount, createdAt, thumbnailUrl?}], nextCursor}` | — |
| `GET /api/resources/{id}` | owner | full resource + `scan:{status, engine, scannedAt}` + `file?` / `media?` | `404` |
| `GET /api/resources/{id}/status` | owner | `{processingStatus, rejectedReason?, derivatives:[{kind,status}]}` (desktop/CLI poll; `Retry-After` hint) | `404` |
| `PATCH /api/resources/{id}` | owner | `{title?, expiresAt?, burnAfterDownload?(only before claim), password?/clearPassword?}` | `400 expiry-beyond-plan`, `409 not-active`, `409 burn-already-claimed` |
| `DELETE /api/resources/{id}` | owner | `204`; bytes queued for deletion immediately (Q2) | `404` |
| `GET /api/media/{id}` / `GET /api/public/media/{code}` | owner / P | `{width, height, durationMs, frameCount, hasAlpha, colorSpace, iccProfileName, mediaType, browserPlayable, derivatives:[{kind, width, height, url (ticketed)}]}` | `401`, `404`, `410` |
| `GET /api/public/resources/{code}` | P | descriptor `{kind, title, sizeBytes, contentType, originalName, state, requiresPassword, burnAfterDownload, burned, expiresAt, preview?}` — **never** a download URL | `404`, `410 expired|burned|disabled` |
| `POST /api/public/resources/{code}/unlock` | P, rate `unlock` | `{password}` → `200 {viewerToken, expiresAt}` (HMAC, 15 min, bound to resource id) | `401 password-incorrect`, `410` |
| `POST /api/public/resources/{code}/download` | P (+viewerToken if locked, optional `Captcha-Token` for burn, Q12) | `{disposition:"attachment"|"inline", derivative?}` → `200 {url:"https://<content-host>/d/{ticket}", expiresAt}` (ticket TTL 120 s) | `409 processing`, `410 burned|expired`, `422 content-rejected`, `429` |
| `POST /api/resources/{id}/download` | owner | same; owner grants never burn | — |
| `GET|HEAD https://<content-host>/d/{ticket}` | T | API validates ticket + grant, then `X-Accel-Redirect: /_protected/objects/3f/a2/<uuid>`, `Content-Type` (attachment: `application/octet-stream`; inline only for allowlisted raster/video), `Content-Disposition: attachment; filename="…"; filename*=UTF-8''…`, `Cache-Control: private, no-store`, `X-Content-Type-Options: nosniff`, `CSP: sandbox; default-src 'none'`, `Referrer-Policy: no-referrer`; Nginx serves bytes + `Range` | `403 ticket-invalid`, `410 burned|expired`, `404` |
| `GET /api/me/storage` | J, K(resources:read) | `{plan, maxFileBytes, maxSnippetBytes, retentionDays, quotaBytes, usedBytes, reservedBytes, openUploads}` | — |

### 3.4 CLI / desktop
- Auth: API keys are Starter-only (`IdentityService.cs:395`) and Free users must be able to use the CLI/desktop (PRD: 30 % install target). Options (Q5): (a) OAuth 2.0 Device Authorization Grant (`POST /api/auth/device/code` → `{deviceCode,userCode,verificationUri,interval}`; `POST /api/auth/device/token` → existing token pair) for both CLI and Tauri; (b) allow scoped keys on Free with only `snippets:write files:write resources:read`. Recommend (a) + existing refresh rotation; keys stay for automation.
- New scopes: `snippets:write`, `files:write`, `resources:read` (+ existing `links:*`, `stats:read`); JWT sessions get all (`TokenService.cs:60`).
- `GET /api/clients/config` (public): `{minCliVersion, minDesktopVersion, uploadEndpoint, maxChunkBytes, tusVersion}` so the AOT CLI and Tauri app don't hard-code hosts.
- AOT note: the CLI should implement tus by hand (POST/HEAD/PATCH are simple) with `System.Text.Json` source-gen; backend keeps responses free of polymorphic JSON.
- `User-Agent: zaagl-cli/<ver>` / `zaagl-desktop/<ver>` → `created_via`; never used for security decisions (R10).

### 3.5 Rate limits & idempotency
New policies in `Program.cs:69-86` style (abuse throttles only; quotas are checked in-transaction per spec §7.2 step 9 / MUST NOT #19): `content-write` 60/min per `sub` (snippets, complete), `upload-create` 30/min per `sub`, `download-ticket` 30/min per IP-partition+code, `unlock` reused (10/min). Nginx on the content host: `limit_conn` per IP, `limit_rate` per plan via `X-Accel-Limit-Rate`. Idempotency-Key honoured on `POST /api/snippets`, `/api/snippets/raw`, `POST /api/uploads`, `/complete`, `/uploads/simple`; scope `u:<id>`, hash = SHA-256 over a canonical request (content hash for snippets; session id + metadata for complete).

---

## Part B: client authentication for CLI and desktop (desktop/CLI draft)

## 3. Authentication

**Recommendation:** OAuth 2.0 Device Authorization Grant (RFC 8628) as the single P0 mechanism for **both** CLI and desktop; loopback redirect with PKCE (RFC 8252 §7.3) as a P2 UX upgrade for desktop only. Rationale: device flow works over SSH, in containers and headless shells (CLI must), reuses the web app's existing sign-in (password, Google/GitHub, admin TOTP) without new redirect plumbing (A8), and needs one backend feature. Desktop opens `verification_uri_complete` in the browser so the user only clicks "Approve".

Flow: client `POST device-authorize {client_id: zaagl-desktop|zaagl-cli, device_name, os}` -> `{device_code, user_code (8 chars, no vowels/ambiguous, XXXX-XXXX), verification_uri (app.zaa.gl/activate), verification_uri_complete, expires_in 600, interval 5}`. User signs in on the web app (any method), sees client, device name, approximate location/time, approves or denies. Client polls token endpoint with `grant_type=urn:ietf:params:oauth:grant-type:device_code`; errors `authorization_pending`, `slow_down` (+5 s), `access_denied`, `expired_token`. Success returns the existing `AuthResponse` shape plus `sessionId`. Public clients, no client secret in any binary (agent rule). Phishing mitigation: approval page shows the device name and warns "only approve if you started this on your own computer"; codes single-use, 10 min.

Token rules for native clients (BE-AUTH-NATIVE): JWT carries `client_id`/`amr=device`; scopes `links:read links:write stats:read content:write` (new scope for snippets/files); **excluded from the "Interactive" policy** (no API-key management, billing, account deletion/export) and from the Admin policy; Admin users can sign in but get a non-admin token (Q8). Refresh family records `client_id`, `device_name`, `created_at`, `last_used_at` (G5) with list/revoke endpoints, shown in web Settings -> "Devices".

Storage: refresh token only, in the OS store: Windows Credential Manager generic credential (`target = zaa.gl/desktop/<userId>`; blob limit 2,560 bytes [unverified exact], refresh token is ~43 chars so fine), macOS Keychain generic password (service `gl.zaa.desktop`, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, not synchronised to iCloud), Linux Secret Service (CLI only). Access token in memory only. Desktop and CLI use separate entries and separate sessions. Nothing secret in settings, logs, crash dumps or the upload journal.

Refresh: proactive at 80 % of access-token life and reactive on 401; single-flight in-process; the CLI additionally takes a cross-process lock (named mutex on Windows, `flock` on a lock file containing no secret on Unix) because parallel `zaagl` invocations in one shell would otherwise trip reuse detection (A3) and revoke the family. On `refresh_raced` re-read the store. Write new refresh token to the store **before** using the new access token.

Sign-out: `POST logout {refreshToken}` (exists, A5), then delete the keyring entry and caches even if offline (best-effort server call retried next start). Revocation from the web "Devices" page or logout-everywhere surfaces as 401 + failed refresh -> client clears store and shows signed-out state. Account disabled/deleted -> same.

API keys (CLI only, optional): `zaagl login --with-api-key` reads the key from stdin, or `ZAAGL_API_KEY` env var for CI; premium-only per spec (A11), so the device-flow session is the free-tier path. Depends on BE-AUTH-SCOPES being fixed (A13).

Backend capabilities required: **BE-AUTH-DEVICE** (device authorize + token grant + approve/deny API, own rate limiter, A19), **FE-ACTIVATE** (web `/activate` page), **BE-AUTH-NATIVE** (native token claims, policy exclusion), **BE-AUTH-SESSIONS** (family metadata, list/revoke), **BE-AUTH-SCOPES** (enforce scopes on links + content endpoints), **BE-AUTH-LOOPBACK** (P2: authorize endpoint with PKCE S256, loopback `http://127.0.0.1:{port}/cb` any-port match, state, one-time code, code exchange).

---


## Part C: calls the frontend needs, to reconcile with Part A (frontend draft)

## 5. API calls needed (to reconcile with BE contract)

Conventions: `/api` prefix as implemented (A7), JSON camelCase, ProblemDetails `{title,status,code}`; anonymous viewer calls are made **server-side** by Next (internal network) or via same-origin route handlers — no browser CORS to the API.

| Id | Method & route | Payload → response | Notes |
|---|---|---|---|
| API-S1 | `POST /api/snippets` | `{content, kind:"code"\|"text"\|"markdown", language?, filename?, title?, customSlug?, folderId?, tags?, policy:{password?, expiresAt?, maxViews?, burnAfterRead?}}` → `Snippet{id, slug, kind, language, title, version:1, sizeBytes, lineCount, policy, createdAt}` | 413 `too_large`, 422 validation, 409 `alias-unavailable` |
| API-S2 | `GET/PATCH/DELETE /api/snippets/{id}` | owner; PATCH metadata/policy only | |
| API-S3 | `POST /api/snippets/{id}/versions` | `{content, language?}` → `SnippetVersion{number, createdAt, sizeBytes, lineCount}` | versions immutable |
| API-S4 | `GET /api/snippets/{id}/versions` · `GET …/versions/{n}` | list · `{content, language}` | |
| API-S5 | `GET /api/snippets/{id}/diff?from=&to=` | `{hunks:[{oldStart,newStart,lines:[{type:"ctx"\|"add"\|"del", text}]}], stats}` | DiffPlex |
| API-U1 | `POST /api/uploads` | `{filename, sizeBytes, mimeType, kind:"image"\|"video"\|"file", expiresInDays?, burnAfterDownload?, policy?, customSlug?, folderId?}` → `{uploadId, tusEndpoint, uploadToken, uploadTokenExpiresAt, maxBytes}` | quota → 402/413 with `code` (`plan_limit`, `storage_full`) |
| API-U2 | tus `POST/HEAD/PATCH/DELETE {tusEndpoint}` | headers `Tus-Resumable: 1.0.0`, `Upload-Length`, `Upload-Metadata: uploadId,filename,filetype`, `Authorization: Bearer <uploadToken>` | files host, CORS for app origin, expose `Location, Upload-Offset` |
| API-U3 | `POST /api/uploads/{id}/token` | → new `{uploadToken, uploadTokenExpiresAt}` | resume |
| API-U4 | `GET /api/uploads/{id}` | → `{status:"uploading"\|"scanning"\|"processing"\|"ready"\|"rejected", rejectReason?, resource?}` + SignalR event `uploadStatus` on existing hub | |
| API-R1 | `GET /api/resources?kind=&search=&status=&page=&pageSize=` | → `Paged<ResourceListItem{id, kind, slug, title, filename, mime, sizeBytes, width?, height?, durationMs?, thumbnailUrl (signed, ≥15 min), status, views, downloads, expiresAt, burnAfterDownload}>` | or extend `/api/links` with `kind` (ARC-xxx(resources)) |
| API-R2 | `GET/PATCH/DELETE /api/resources/{id}` | detail / policy edits / delete | |
| API-R3 | `GET /api/links?kind=` and `Link.kind`, `Link.resourceId` | add `kind:"url"\|"snippet"\|"image"\|"file"` | library integration |
| API-R4 | `POST /api/resources/{id}/download-url` | → `{url, expiresAt}` | owner download/preview (A6) |
| API-B1 | `GET /api/billing/me` (extend) | add `tier` 2=Pro, `limits.{maxFileBytes, maxSnippetBytes, storageQuotaBytes, maxRetentionDays, burnAllowed}`, `usage.storageUsedBytes` | A14 |
| API-V1 | `GET /api/public/content/{slug}` (anonymous, **never consumes**) | → `{kind, slug, title?, state, createdAt, expiresAt, gate:{password:boolean, reveal:boolean}, snippet?:{language, kind, versions:number[], current, lineCount, sizeBytes, content? (only if ungated & not reveal)}, media?:{mime, width, height, durationMs?, src, posterUrl?, variants:[{mime, src}]}, file?:{filename, mime, sizeBytes, scanned:boolean, previewKind:"pdf"\|"image"\|"video"\|"none", previewUrl?, downloadsLeft?}}` | `src/previewUrl` = content-domain signed URLs |
| API-V2 | `POST /api/public/content/{slug}/unlock` | `{password}` → `{grant, grantExpiresAt, ...same payload unlocked}` | 401 wrong, 429, 410 |
| API-V3 | `POST /api/public/content/{slug}/reveal` | `{grant?}` → unlocked payload; consumes a burn-after-read view | explicit click only |
| API-V4 | `GET /api/public/content/{slug}/versions/{n}` · `…/diff?from&to` | header `X-Zaagl-Grant` if gated | |
| API-V5 | `POST /api/public/content/{slug}/download` | `{grant?}` → `{url (single-use, ~60 s), burned:boolean}` | consumes for burn |
| API-V6 | `POST /api/reports` | `{slug, reason, details?, captchaToken}` | spec §7.15 |
| — | `GET {content-domain}/…signed…` | bytes, `Range` support | Nginx X-Accel |

---

