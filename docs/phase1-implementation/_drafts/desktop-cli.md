# Phase 1 draft: E1.4 desktop tray app and E1.1 developer CLI

Status: DRAFT for review. Author role: Desktop & CLI engineer. Date: 2026-11-09.
Sources: PRD `Zaagl-Frontend/src/Doc/zaagl-prd.html` (E1.1, E1.4, risk register, pricing table), tech stack `zaagl-tech-stack (2).html` (Tauri 2 + React, .NET Native AOT CLI, tus/tusdotnet uploads, separate files hostname, ClamAV), spec `docs/spec/zaagl-spec-v9.4.md`, backend code at `src/`.
Conventions: `[verified]` = checked in code/docs this session; `[unverified]` = from memory of third-party behaviour, must be confirmed before implementation. Backend work is referenced as generic `BE-*`/`ARC-*` capabilities, not routes; the backend/upload drafts own the final shapes.

---

## 1. Audit findings (auth and API surface the clients must use)

### 1.1 What exists [verified]
| # | Finding | Reference |
|---|---|---|
| A1 | Access token: HS256 JWT, 15 min (`ExpiryMinutes = 15`), claims `sub`, `email`, `name`, `sstamp`, role, and **all three scopes** for every interactive session. | `src/Modules/Identity/Application/TokenService.cs:32`, `:49-75`, `:60` |
| A2 | JWT validation re-checks `sstamp` against the user's current security stamp, so logout-everywhere / password reset kills outstanding access tokens within ~60 s (cached). Clients must treat a 401 on a non-expired token as "refresh, else sign in again". | `src/Zaagl.Api/Program.cs:124-136` |
| A3 | Refresh token: opaque, 30-day sliding, rotated on every use, family-based reuse detection. Re-use within 30 s returns error code `refresh_raced`; later reuse **revokes the whole family**. There is no absolute session lifetime (rotation slides forever). | `IdentityService.cs:218-232`, `:239-273` |
| A4 | `refresh_raced` is not in the status map, so it is returned as **400** (default arm), while the web client's comment assumes 409. Clients must branch on the problem-details `code` extension, not on status. | `src/Zaagl.Api/Infrastructure/ApiResults.cs:10-26`; `Zaagl-Frontend/src/lib/api-client.ts:41` |
| A5 | `POST /api/auth/logout {refreshToken}` revokes that token's family, anonymous, idempotent: usable for desktop/CLI sign-out. `logout-everywhere` revokes all families **and all API keys**. | `IdentityEndpoints.cs:87-98`; `IdentityService.cs:275-289` |
| A6 | `RefreshToken` rows have no client id, device name, user agent or last-used time, so a user cannot see or revoke "Zaagl Desktop on LAPTOP-7" individually. | `src/Modules/Identity/Domain/User.cs:89-98` |
| A7 | `POST /api/auth/login` is email+password (+TOTP for admins, enrol-only token path). Technically callable from a native app, but it excludes social-only accounts (`PasswordHash` null), forces the app to handle passwords/TOTP and would put a password prompt in a terminal. Not recommended (agent rule: device/loopback only). | `IdentityEndpoints.cs:73-81`; `IdentityService.cs:181-216` |
| A8 | Social sign-in: `/api/auth/external/{provider}` -> provider -> `/api/auth/external/callback` which redirects to the **fixed** `Frontend:BaseUrl/auth/callback#token=..&refresh=..`. No `redirect_uri`, `state` or PKCE parameter; tokens land in browser history. Cannot be pointed at a loopback listener without new work, and adding a free `redirect_uri` would be a token-exfiltration open redirect. | `IdentityEndpoints.cs:116-153` |
| A9 | **No device-authorization grant, no loopback/PKCE flow, no authorization-code endpoint.** Grep for `device_code`, `code_challenge`, `pkce`, `loopback`, `urn:ietf` found only the spec text, the agent file and an unrelated `UrlSafety.cs` hit. | repo-wide grep |
| A10 | API keys: header `X-Api-Key`, prefix `zgl_`, max 20, SHA hash stored, `LastUsedAt` throttled to 5 min, no expiry. The "Smart" scheme forwards to the key handler whenever the header is present. | `Infrastructure/ApiKeyAuthentication.cs:9-35`; `Program.cs:95-98`; `IdentityService.cs:35-39`, `:404-415`, `:556-567` |
| A11 | Key creation is **premium-only**: tier must be `Starter` (the build's paid tier mapped to spec "premium"), else `plan_required` -> **402**. Key management is JWT-only ("Interactive" policy). Spec plan table: API keys ✗ guest, ✗ free, ✓ premium. | `IdentityService.cs:389-395`; `IdentityEndpoints.cs:155-165`; `Program.cs:176-178`; spec line 29, 596 |
| A12 | Key validation does **not** re-check the plan, so keys of a downgraded user keep working (spec does not say otherwise; needs a product decision). | `IdentityService.cs:556-567` |
| A13 | **Scopes are issued but never enforced.** Key scopes become `scope` claims, but no endpoint/policy checks them; `/api/links` group uses plain `RequireAuthorization()`. A `links:read`-only key can create, edit and delete links. Spec requires `links:write` for create/edit/delete (spec §7.2 step 1, access table lines 591-595). Security gap; must be fixed before we tell users to mint scoped CLI keys. | `LinksEndpoints.cs:24`, `:36-98`; grep for `RequireClaim`/scope policy: none |
| A14 | The "Interactive" policy accepts **any** JWT bearer. A token minted for the CLI/desktop would therefore be able to manage API keys, billing and account export. Native-client tokens need a narrower policy (see §3). | `Program.cs:174-178`; `IdentityEndpoints.cs:156`, `:201` |
| A15 | `POST /api/links`: honours `Idempotency-Key` (good for retries); authenticated -> account link; anonymous -> guest link that **requires `Captcha-Token`**, so a native client cannot use guest mode. Invalid credentials -> 401 (never a silent guest downgrade, spec §7.2). Rate limit `link-write` 60/min per `sub`. | `LinksEndpoints.cs:36-81`; `Program.cs:83` |
| A16 | `LinkDto` has `Slug` but **no `shortUrl`** (spec §5 link resource has one). Clients would have to hard-code the short host; we need the server to return the absolute link. | `src/Modules/Links/Contracts/LinkContracts.cs:61-76` |
| A17 | Routes are `/api/*`, not the spec's `/api/v1/*`, by recorded decision; single dev host. Public native clients will pin whatever we ship, so the prefix must be settled before CLI 1.0. | `docs/DECISIONS.md:29-34` |
| A18 | CORS policy "web" with credentials for configured origins; spec §1.2 says `api.zaa.gl` is "Bearer tokens and API keys only; no cookies; no CORS". Desktop design below makes all HTTP calls from Rust, so no CORS/origin change is needed for the Tauri webview. | `Program.cs:63-67`; spec line 54 |
| A19 | `auth` limiter is 20/min per IP; a device-code poll endpoint must get its own limiter (RFC 8628 `slow_down`), not share this one. | `Program.cs:82` |
| A20 | Content modules `Files`, `Snippets`, `Media`, `DeveloperTools` are **empty scaffolds** (`.gitkeep` only). Every snippet/file/image capability the clients need is still to be built. | `src/Modules/{Files,Snippets,Media,DeveloperTools}/` |

### 1.2 Frontend reuse for the Tauri UI [verified]
- Stack: Next 16.4, React 19.3, MUI 9.5 + Emotion, Tailwind 4, TanStack Query 5, qrcode.react, SignalR (`Zaagl-Frontend/package.json`).
- Portable as-is (no Next imports): `src/components/ui/{toast,confirm-dialog,menu,status-chip,icons,collapsible-card}.tsx`, `src/lib/{theme.tsx,format.ts,types.ts,query-client.ts}`, `src/lib/i18n/context.tsx` + `src/messages/{en,ar}.json` (RTL support), `src/features/links/QrPreview.tsx`.
- Needs a shim: `components/ui/controls.tsx:3` and `components/branded-card.tsx:3` import `next/link`; `lib/mui-bridge.tsx:5` uses `next/navigation` (SSR style insertion, not needed in Tauri); `lib/config.ts` reads `process.env.NEXT_PUBLIC_*`.
- **Must not be reused:** `lib/token-store.ts:35-36` keeps access+refresh tokens in `localStorage`, and `lib/api-client.ts` calls the API from JS with that store. In the desktop app tokens live only in Rust (§2.1).
- `features/files/*` and `features/snippets/*` (Uploader, LanguagePicker, SnippetViewer, ...) are **0-line placeholders**, so there is nothing to share yet for upload UI; coordinate with the frontend media draft so `LanguagePicker` and an upload-progress row are written framework-agnostic.
- Recommendation: an npm workspace package `@zaagl/ui` (ui primitives, theme, i18n, types, format) consumed by Next and Tauri (Vite). A `LinkLike` component prop replaces `next/link`. Decision belongs to the frontend lead (open question Q9).

### 1.3 Gaps that block the clients
G1 no device/loopback flow (A9, A8). G2 free users cannot use API keys (A11), so the CLI's only free-tier path is an interactive native session; CI usage on free tier is impossible. G3 scopes unenforced (A13). G4 native tokens would be over-privileged (A14) and Admin JWTs could leave the admin network context via a desktop session. G5 no session inventory/per-device revoke (A6). G6 no `shortUrl` (A16). G7 no snippet/file/upload/list-my-content APIs (A20). G8 no capabilities/limits endpoint so clients cannot pre-check size or expiry against plan (pricing table: 25 MB/7 d free, 100 MB/30 d Starter, 5 GB Pro). G9 API prefix/versioning undecided for public clients (A17). G10 no min-client-version signal.

---

## 2. Desktop architecture (Tauri 2)

### 2.1 Structure and responsibilities
```
apps/desktop/
  src-tauri/            Rust: everything privileged
    src/main.rs         builder, plugins, single-instance, tray
    src/shortcut.rs     register/rebind/conflict reporting
    src/clipboard/      snapshot (win.rs, mac.rs), classify.rs (pure, unit-tested)
    src/api/            reqwest client, error mapping, token manager (single-flight refresh)
    src/auth/           device flow (+ loopback PKCE later), keyring storage
    src/upload/         tus 1.0 client (create, PATCH, HEAD resume, terminate), job queue, journal
    src/share.rs        pipeline: snapshot -> classify -> preflight -> send -> copy -> chime/notify
    capabilities/*.json per-window allowlists (only our commands; no fs/shell/http plugins exposed to JS)
    tauri.conf.json     CSP default-src 'self'; no remote content; devtools off in release
  src/ (React + Vite)   settings, sign-in (shows user code), recent shares, upload progress, onboarding, about
```
- **Rust owns:** tray/menu, global shortcut, clipboard read/write, all network I/O, token storage and refresh, upload state, notifications, sound, autostart, updater. **Web owns:** presentation only; it calls `invoke("share_now")`, `invoke("get_settings")`, listens to `share://progress` events. The webview never sees an access or refresh token, so XSS in the UI cannot exfiltrate credentials, and no CORS change is needed (A18).
- Plugins (official Tauri 2): `global-shortcut`, `clipboard-manager` (text + image only), `notification`, `autostart`, `updater`, `single-instance`, `store` (non-secret settings), `log`, `opener`. Crates: `keyring` (Credential Manager / Keychain), `reqwest` (rustls), `clipboard-win` + `objc2-app-kit` for file lists and format inspection, `image` (PNG encode), `rodio` (chime). [unverified: exact crate versions; `clipboard-manager` lacking file-list read is per its docs as of 2.x]
- No Dock icon on macOS (`LSUIElement`/`ActivationPolicy::Accessory`); on Windows no taskbar button, tray only.

### 2.2 Tray / menu bar
Menu: Share clipboard (shows shortcut), Recent shares (last 10: click = copy, alt = open), Upload file..., Pause uploads / Resume, Signed in as x@y / Sign in, Settings, Check for updates, Quit. Tray icon states: idle, working (animated or badge), error (until acknowledged), signed-out. Left-click on Windows opens the recent-shares popover; macOS click opens the menu.

### 2.3 Global shortcut
- Default per PRD: Ctrl+Shift+Z (Win) / Cmd+Shift+Z (mac). **Conflict:** this is Redo in macOS system-wide and in VS Code, Figma, Photoshop, browsers, Office on Windows. A global registration (`RegisterHotKey` / Carbon `RegisterEventHotKey`) swallows it in every app, i.e. Redo stops working for our users. Proposal: keep the PRD default but (a) explain in onboarding with one-click alternatives (Ctrl+Alt+Z / Cmd+Ctrl+Z), (b) make it rebindable, (c) product decision Q1.
- Registration failure (another app owns it; Windows `ERROR_HOTKEY_ALREADY_REGISTERED`) -> tray shows warning, settings shows "in use by another app", share still available from the menu. Re-register after sleep/resume and session unlock. Debounce 500 ms; a press while a share is in progress queues one more share (max 2 concurrent uploads, rest queued).
- macOS: Carbon hotkeys need **no** Accessibility or Input Monitoring permission [unverified for macOS 26; verify on beta].

### 2.4 Clipboard detection order (pure `classify(ClipboardSnapshot)`)
Snapshot first (one read of all formats with timestamps/sequence number: Windows `GetClipboardSequenceNumber`, mac `changeCount`), then classify:
1. **File list** — Win `CF_HDROP`; mac `public.file-url` items. One file -> file upload (image MIME -> image viewer type). Multiple files or a folder -> P1 behaviour per Q4 (proposed: zip client-side into one upload, confirm if > 200 files or > plan limit). Unreadable/locked file -> error toast.
2. **Image bitmap** — Win: registered `PNG` format preferred, else `CF_DIBV5`/`CF_DIB` (convert, keep alpha); mac: `public.png`, else `public.tiff` -> PNG. Covers Win+Shift+S (Snipping Tool), PrtScn, mac Cmd+Ctrl+Shift+3/4, "Copy image" in browsers. Encoded PNG is written to the app cache so the upload is resumable. Heuristic exception: if the clipboard also holds rich text (`CF_HTML`/RTF/`public.html`) plus plain text, it is a document copy (Excel/Word/Pages put a bitmap rendering alongside) -> treat as text. Mark heuristic, tune in manual matrix.
3. **URL** — plain text trimmed, single line, absolute `http`/`https`, length <= server max -> short link via the link API. `zaa.gl` links themselves -> notify "already a Zaagl link" (no re-shortening).
4. **Text/code** — non-empty text -> snippet; language detection server-side with optional client hint (none from desktop in P1). Enforce plan max snippet size before sending. Optional P2: warn if text looks like secrets (`-----BEGIN`, `AKIA`, `.env` lines) since links are shareable.
5. Empty/unsupported (e.g. only private formats) -> notification "Nothing to share on the clipboard".
macOS pasteboard privacy: Apple has been adding user-visible prompts for programmatic pasteboard reads [unverified, check macOS 26]; reading only on an explicit shortcut press is the defensible pattern; never poll the clipboard.

### 2.5 Screenshots
The PRD flow reads a screenshot that is **already on the clipboard**; the app does not capture the screen, so it needs no Screen Recording (TCC) permission. A built-in region capture is out of scope for P1 (would add the macOS Screen Recording prompt; see Q6).

### 2.6 Share pipeline, upload, progress, resumability
1. Preflight: cached capabilities (G8: plan, max file/snippet bytes, default/max expiry; refreshed hourly and on 402). Over limit -> notify with upgrade link, no upload.
2. URL/snippet: single request with `Idempotency-Key` = per-share UUID (A15); target < 1 s end to end (PRD).
3. File/image: create upload session (name, size, MIME, optional SHA-256) -> server returns tus URL on the files host (tech stack: separate DNS-only hostname, ASP.NET streaming, tusdotnet). PATCH in 8 MB chunks (adaptive 2-32 MB), progress events to UI and tray tooltip. Journal (non-secret: upload URL, path, size, mtime, offset) in app data; on network loss retry with backoff, on app restart `HEAD` + resume; source file changed (size/mtime) -> restart upload. Cancel -> tus termination. Uploads also authenticate with the bearer token (refresh mid-upload is transparent).
4. Finalise -> server returns the share's `shortUrl` (G6). If malware scanning is asynchronous, the link must already be valid and show "processing" (backend decision, Q7).
5. Copy: write text to clipboard (optionally also `text/html` anchor); for uploads finishing > 10 s after the press, setting "copy when done" (default on) plus notification, so the user is not surprised by a late clipboard change.
6. Chime (bundled ~150 ms WAV via `rodio`, volume follows system; off switch) + notification "Link copied - zaa.gl/abc - expires in 7 days"; clicking opens the link. Respect Focus/DND (OS handles notification suppression; chime suppressed when OS reports DND where detectable [unverified]).
7. Add to recent shares (local, last 50, no content stored, only link + type + time).

### 2.7 Offline and error behaviour
Error mapping from problem-details `code`: network/DNS/TLS -> "Offline, retrying" (uploads auto-resume on reconnect; URL/snippet payload kept in memory for 10 min with a Retry action, **no silent background queue**, the clipboard may have moved on); 401 -> one refresh (single-flight mutex) -> retry, else "Sign in again" opening sign-in; `refresh_raced` -> re-read keyring then retry; 402 `plan_required` -> upgrade notification; 413/validation -> "Too large for your plan (max X)"; 429 -> honour `Retry-After`; 5xx/503 -> backoff x3 then notify. All failures logged locally (rotating, no tokens/content, Q10 for telemetry). Corporate proxies: use system proxy (reqwest system-proxy support [unverified on Windows PAC]) and OS trust store (`rustls-platform-verifier`) so TLS-inspecting proxies work.

### 2.8 Auto-start, auto-update, settings
- Autostart via plugin (Windows HKCU Run; macOS login item, prefer `SMAppService` on 13+ [unverified whether plugin uses it]); asked in onboarding, default on.
- Updater: `tauri-plugin-updater` with minisign-signed manifest (key separate from OS signing certs, private key only in CI secret store), static JSON on GitHub Releases or `updates.zaa.gl`; channels stable/beta; check at start + every 6 h; download in background, install on user consent or quit; staged rollout by percentage in manifest generator; server can force minimum version (BE-CLIENTVER).
- Settings (plugin `store`, non-secret): shortcut, default expiry (bounded by plan), password-protect uploads by default (P2), copy format (plain / Markdown), chime on/off, notifications on/off, "copy when done", max concurrent uploads, autostart, language (en/ar, RTL), theme, update channel, hidden dev: API base URL (dev builds only).

---

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

## 4. CLI (.NET 10 Native AOT)

Commands (stdout = result only, everything else to stderr):
| Command | Behaviour |
|---|---|
| `zaagl login [--no-browser] [--with-api-key]` | Device flow; prints code + URL to stderr, opens browser unless `--no-browser`/no display; `--with-api-key` reads key from stdin. |
| `zaagl logout` / `zaagl whoami` | Revoke + clear store / show account, plan, auth method, session device name. |
| `zaagl share <path>... [--expires 7d] [--title] [--password-stdin]` | Upload file(s) via tus with stderr progress bar; multiple paths per Q4. Prints the link. |
| `<cmd> \| zaagl paste [--lang auto\|<id>] [--title] [--name file.ext] [--expires] [--tee]` | Read stdin to EOF (cap = plan max, abort with exit 8 beyond), create snippet; `--tee` passes input through to stderr so the user still sees output. Also `zaagl paste < file.log`. Strips ANSI escapes by default (`--keep-ansi`). |
| `zaagl link <url> [--slug] [--expires]` | Short link. |
| `zaagl ls [--type link\|snippet\|file] [--limit 20] [--page]` | List own shares (BE-CONTENT-LIST). |
| `zaagl rm <code>` (P2), `zaagl open <code>` (P2) | Delete / open in browser. |
| `zaagl config get\|set\|list\|unset <key>` | Non-secret settings: `api-url`, `default-expiry`, `output`, `color`, `update-check`. File: `%APPDATA%\zaagl\config.json`, `~/Library/Application Support/zaagl/config.json`, `$XDG_CONFIG_HOME/zaagl/config.json`. |
| `zaagl version [--check]`, `zaagl completion bash\|zsh\|fish\|pwsh` | Version, optional release check, shell completions. |

Output: human text when stdout is a TTY; `--json` emits one JSON object (or JSON Lines for `ls --json`); `--quiet` prints only the link; `NO_COLOR`/`--no-color` honoured; progress only when stderr is a TTY. Link is the last stdout line so `url=$(zaagl paste < x)` works. Optional `--copy` puts the link on the clipboard (P2; Windows `clip`-equivalent via Win32, mac `NSPasteboard`, Linux skip).
Exit codes: 0 ok; 1 unexpected; 2 usage; 3 not signed in / auth failed / revoked; 4 plan or quota (`plan_required`, daily limit); 5 not found; 6 network or server unavailable (retryable); 7 rate limited; 8 validation / too large; 130 interrupted (Ctrl+C cancels upload and terminates tus session unless `--keep-partial`).
Credentials: precedence `ZAAGL_API_KEY` env > `ZAAGL_TOKEN` (short-lived, CI) > OS store. Windows `CredWriteW/CredReadW/CredDeleteW` via `[LibraryImport("advapi32")]`; macOS `SecItemAdd/CopyMatching/Delete` via `[LibraryImport]` to Security.framework (do **not** shell out to `security add-generic-password -w`, which exposes the secret in argv); Linux libsecret `secret_password_store_sync` via `NativeLibrary.TryLoad`; if no Secret Service (headless server) the CLI refuses to persist and tells the user to use env vars (agent rule: no plain files; Q5).
Native AOT constraints: `PublishAot`, `IsAotCompatible`, `InvariantGlobalization` (CLI messages English-only in P1), `JsonSerializerIsReflectionEnabledByDefault=false` with one `JsonSerializerContext` covering every DTO and problem-details; IL2xxx/IL3xxx warnings as errors; no reflection DI, no `ConfigurationBinder` (or its source generator); `System.CommandLine` 2.x for parsing [unverified AOT-clean, else hand-rolled parser]; one static `HttpClient` over `SocketsHttpHandler` (`PooledConnectionLifetime` 5 min, HTTP/2, `AutomaticDecompression`), system proxy honoured by default; tus client hand-written (~300 LoC: creation, PATCH with `Upload-Offset`, HEAD resume, termination) since existing .NET tus client packages are not known to be AOT-annotated [unverified]; DTOs hand-written and checked against the API's OpenAPI document in CI (no runtime codegen). Binary size target < 15 MB, cold start < 50 ms.
Builds: AOT cannot cross-compile across OSes, so a CI matrix of native runners: `win-x64`, `win-arm64` (cross-arch on Windows runner), `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `linux-musl-x64` (Alpine/containers). Static release archives + SHA256SUMS + signatures.

---

## 5. Packaging, signing, distribution, updates, permissions

- **Windows desktop:** Tauri NSIS installer, per-user, no admin, WebView2 Evergreen bootstrapper; MSI (WiX) for enterprise/Intune (P2). Authenticode-sign the app exe, installer and updater bundles. PRD budgets an EV certificate; note: EV keys must live on an HSM since 2023, so CI signing needs a cloud HSM (Azure Key Vault, DigiCert KeyLocker, SSL.com eSigner) or Azure Trusted Signing as a cheaper alternative, and EV reportedly no longer grants instant SmartScreen reputation [unverified, re-check before buying] (Q2).
- **macOS desktop:** universal `.app` (aarch64 + x86_64), Developer ID Application certificate ($99/yr Apple Developer Program per PRD), hardened runtime, no App Sandbox (Developer ID distribution), entitlements: none beyond defaults (outgoing network is not restricted outside the sandbox); `notarytool submit --wait`, staple the `.dmg` and the updater `.app.tar.gz` contents. Minimum macOS 12 (Tauri 2 supports 10.15+; Q11).
- **CLI:** Windows binaries Authenticode-signed; macOS binaries codesigned with hardened runtime and notarised (bare Mach-O cannot be stapled, Gatekeeper checks online; Homebrew installs are not quarantined anyway); Linux: SHA256SUMS signed with minisign/cosign.
- **Channels:** GitHub Releases (source of truth for both, release notes), `zaa.gl/download` page; **winget** (`Zaagl.Desktop`, `Zaagl.CLI`) manifests submitted to `microsoft/winget-pkgs` automatically from the release workflow; **Homebrew** tap `zaagl/homebrew-tap` with a cask (desktop) and a formula (CLI) updated by CI; Scoop bucket (P3); Linux tarball (P1), `.deb`/`.rpm` (P3); possible `dotnet tool install -g zaagl` using .NET 10 RID-specific/AOT tool packages [unverified] (P3).
- **Updates:** desktop via signed updater manifest (§2.8); CLI does not self-update in P1: daily cached release check (off in CI when `CI` env is set or `update-check=false`), package managers upgrade. Server-side minimum version (BE-CLIENTVER) returns a dedicated error code so old clients print "please upgrade".
- **OS permissions (minimal):** macOS: notifications (prompted on first notification); no Accessibility, Input Monitoring, Screen Recording, Full Disk Access, camera or mic. Possible pasteboard-access prompt on newer macOS [unverified]. Windows: none, no admin, no UAC. Login item visible in system settings.
- **Release gate:** unsigned or un-notarised artefacts fail the release workflow (agent rule / PRD risk register).

---

## 6. Testing strategy

- **Rust unit:** `classify()` table tests from recorded clipboard fixtures (format lists + payloads captured on each OS, including Excel/Word/Snipping Tool/Finder cases); URL detection; tus state machine against an in-process mock (resume at offset, 409 offset mismatch, 412/460 checksum, server restart); token manager (single-flight under 20 concurrent 401s, `refresh_raced`, family revoked); error-code mapping; settings migration.
- **Web unit:** Vitest + Testing Library for settings, sign-in code screen, progress list; Tauri `invoke` mocked (`@tauri-apps/api/mocks`).
- **CLI unit (xUnit):** arg parsing, output modes (TTY/non-TTY/`--json`), exit-code mapping for every problem `code`, ANSI stripping, size caps, config file read/write, credential-store adapters behind an interface with fakes.
- **AOT gate:** CI publishes AOT on every PR for all RIDs, fails on trim/AOT warnings, then runs the published binary's smoke tests (`version`, `--help`, `paste` against a stub server).
- **Integration (local API):** docker compose of Zaagl-Backend + Postgres + Valkey (+ tusdotnet files host when it exists). Device flow approved by a test helper (dev-only approve endpoint or web-API call with a seeded user). Scenarios: login, link, paste, share 200 MB with forced disconnect and resume, 402 on free over-limit, revoked session -> exit 3, parallel CLI processes refreshing (no family revocation), API key with `links:read` rejected for create (after BE-AUTH-SCOPES). Contract check: diff our DTOs against `/openapi/v1.json` in CI.
- **Desktop E2E:** `tauri-driver` + WebdriverIO on Windows (macOS not supported by tauri-driver [unverified current status]); shortcut and clipboard paths driven by a Rust test harness that sets clipboard contents and invokes the share command.
- **Manual OS matrix (per release candidate):** Windows 10 22H2, Windows 11 24H2 x64 + ARM64, macOS 13, 14, 15, 26 on Apple Silicon + one Intel; sources: Explorer/Finder file copy (1, many, folder), Snipping Tool, PrtScn, mac screenshot to clipboard, browser "copy image", VS Code text, Terminal output, Excel cells, Word rich text, URL from address bar; shortcut conflicts (VS Code, Figma, Office redo); sleep/resume mid-upload; Wi-Fi drop; TLS-inspecting proxy; 200 % DPI tray icon; RTL Arabic UI; install/upgrade/uninstall; SmartScreen and Gatekeeper first-run with release artefacts.

---

## 7. Tasks

Format: **ID - title** (priority) | Description | Depends | Acceptance | Tests.
Generic backend references: BE-AUTH-DEVICE, BE-AUTH-NATIVE, BE-AUTH-SESSIONS, BE-AUTH-SCOPES, BE-AUTH-LOOPBACK, BE-CAPS (plan limits/capabilities endpoint), BE-LINKS-SHORTURL, BE-SNIPPET-CREATE, BE-UPLOAD (upload session + tus + finalise -> link), BE-CONTENT-LIST, BE-CONTENT-DELETE, BE-CLIENTVER, FE-ACTIVATE, ARC-API-PREFIX (`/api` vs `/api/v1` for public clients), ARC-FREE-CLI (free-tier programmatic access policy), ARC-SIGNING (certificate procurement).

### Desktop
- **DSK-001 - Scaffold Tauri 2 app and CI** (P0) | Repo layout §2.1, Vite+React, strict CSP, capabilities allowlist, single-instance, tray-only window model, CI build for win-x64/arm64 and mac universal (unsigned dev artefacts). | - | Builds on both OSes; no Dock/taskbar icon; webview cannot call any non-allowlisted command. | CI build; capability test asserting denied command.
- **DSK-002 - Shared UI package adoption** (P1) | Consume `@zaagl/ui` (theme, i18n en/ar, toast, menu, icons, QrPreview) or vendor copies with a `LinkLike` shim. | Frontend extraction (Q9) | Settings window renders in en/ar with RTL; no `next/*` import in desktop bundle. | Vitest render tests; bundle import lint rule.
- **DSK-003 - API client core** (P0) | reqwest client, base URL config, `X-Zaagl-Client` header, problem-details parsing by `code`, Idempotency-Key helper, system proxy + platform TLS verifier. | ARC-API-PREFIX | All error codes in §2.7 map to typed errors; works behind an authenticated proxy. | Unit tests per code; mock-server tests.
- **DSK-004 - Device-flow sign-in and keyring storage** (P0) | §3 flow, browser opening, polling with `slow_down`, store refresh token in Credential Manager/Keychain, access token in memory. | BE-AUTH-DEVICE, FE-ACTIVATE, BE-AUTH-NATIVE | Sign-in completes with password, Google and GitHub accounts; no token found in app data, logs or webview storage (grep of profile dir). | Unit (poll state machine); integration vs local API; manual secret-scan of profile dirs.
- **DSK-005 - Token manager: refresh, revoke, sign-out** (P0) | Proactive/reactive single-flight refresh, `refresh_raced` handling, sign-out calling logout then wiping store, handle revoked/disabled. | DSK-004, BE-AUTH-SESSIONS | 20 concurrent 401s cause exactly one refresh; revoking the device on the web signs the app out within one request. | Unit concurrency test; integration revoke test.
- **DSK-006 - Tray / menu bar** (P0) | Menu §2.2, icon states, recent shares, signed-out state, HiDPI/dark-mode icons (template images on mac). | DSK-001 | All menu items work; icons crisp at 100-200 %. | Manual matrix; snapshot of menu model in unit test.
- **DSK-007 - Global shortcut with conflict handling** (P0) | Register default, rebind UI, failure reporting, re-register on resume, debounce, onboarding warning about Redo. | DSK-001, Q1 | Shortcut triggers share in background apps; failed registration shown in tray and settings; rebinding persists. | Unit (debounce, accelerator parsing); manual conflict list.
- **DSK-008 - Clipboard snapshot and classifier** (P0) | Native snapshot on Win (`CF_HDROP`, PNG, DIBV5, HTML, text) and mac (`NSPasteboard` types), pure `classify()` per §2.4, PNG normalisation to cache. | DSK-001 | Correct type for every source in the manual matrix; Excel/Word copies classified as text. | Fixture-driven unit tests per OS; manual matrix.
- **DSK-009 - Share URL** (P0) | URL -> link create with idempotency, copy `shortUrl`, notify. | DSK-003, DSK-005, DSK-008, BE-LINKS-SHORTURL, BE-AUTH-SCOPES | Press-to-clipboard < 1 s p95 on broadband; retries never create duplicates; zaa.gl input not re-shortened. | Integration vs local API; timing probe.
- **DSK-010 - Share text/code as snippet** (P0) | Snippet create, size preflight, default expiry, optional secrets warning (P2 flag). | DSK-009, BE-SNIPPET-CREATE, BE-CAPS | Snippet link copied; oversize text gives plan-specific message without network upload. | Integration; unit for preflight.
- **DSK-011 - Resumable upload engine (tus)** (P0) | tus client, chunking, journal, resume after restart, cancel/terminate, concurrency 2, bearer refresh mid-upload. | DSK-003, BE-UPLOAD | 1 GB upload survives a network drop and an app restart and completes without restarting from 0; cancel frees server session. | Mock tus server unit tests; integration with tusdotnet host; manual sleep/resume.
- **DSK-012 - Share file and screenshot** (P0) | File list and image paths through the upload engine; multi-file per Q4; progress UI; copy-when-done. | DSK-008, DSK-011, BE-CAPS | Screenshot from Win+Shift+S and mac Cmd+Ctrl+Shift+4 yields a working image link; file over plan limit refused before upload. | Integration; manual matrix.
- **DSK-013 - Chime and notifications** (P1) | Bundled sound, toggles, notification with open action, error notifications with Retry/Sign in/Upgrade actions. | DSK-009 | Chime plays once per success; disabled when toggled; actions work on both OSes. | Manual; unit for message selection.
- **DSK-014 - Offline and error UX** (P1) | §2.7 behaviour, in-memory retry for 10 min, auto-resume uploads on connectivity change. | DSK-011, DSK-013 | Wi-Fi off/on during each share type behaves as specified; no silent duplicate links. | Integration with fault-injecting proxy (toxiproxy).
- **DSK-015 - Settings window** (P1) | All settings §2.8, plan-bounded expiry picker from capabilities, persisted via store plugin, no secrets. | DSK-002, BE-CAPS | Settings survive restart and upgrade; invalid expiry impossible. | Vitest; settings migration unit test.
- **DSK-016 - Autostart** (P1) | Autostart plugin, onboarding choice, toggle. | DSK-015 | Starts at login on Win 10/11 and macOS 13+; toggle removes login item. | Manual matrix.
- **DSK-017 - Signing and notarisation pipeline** (P0) | Authenticode via cloud HSM/Trusted Signing, mac Developer ID + hardened runtime + notarytool + staple; release fails if unsigned. | ARC-SIGNING | Fresh Windows and macOS installs show no unknown-publisher / Gatekeeper block; `spctl -a -vv` and `signtool verify /pa` pass. | CI verification steps; manual first-run.
- **DSK-018 - Auto-update** (P1) | Updater plugin, minisign keys, manifest generation, channels, staged rollout, min-version handling. | DSK-017, BE-CLIENTVER | v1.0.0 updates to v1.0.1 on both OSes with user consent; tampered manifest rejected; forced-upgrade message shown on min-version error. | CI test with local manifest server; manual.
- **DSK-019 - Distribution channels** (P1) | GitHub Releases, download page links, winget manifest PR automation, Homebrew cask in tap. | DSK-017 | `winget install Zaagl.Desktop` and `brew install --cask zaagl/tap/zaagl` install the signed build. | Manual install check per release.
- **DSK-020 - Desktop E2E and OS matrix** (P1) | tauri-driver suite on Windows, harness-driven clipboard tests, release-candidate manual checklist §6. | DSK-009..012 | Suite green in CI; checklist signed off per RC. | (this task is the tests)
- **DSK-021 - Loopback PKCE sign-in** (P2) | Replace device code UX on desktop with loopback redirect + PKCE when available. | BE-AUTH-LOOPBACK | Sign-in requires one browser approval, no code typing; state/PKCE validated; port bound to 127.0.0.1 only. | Unit (PKCE, state); integration.
- **DSK-022 - Recent shares and quick actions** (P2) | Recent list from local history + `ls` API, copy/open/delete. | BE-CONTENT-LIST, BE-CONTENT-DELETE | Recent list matches server after sign-in on a second machine. | Integration.

### CLI
- **CLI-001 - Scaffold AOT CLI and CI matrix** (P0) | .NET 10 console, AOT flags §4, source-generated JSON context, analyzers as errors, 7-RID native runner matrix, smoke tests on published binaries. | - | All RIDs publish with zero trim/AOT warnings; binary < 15 MB; `zaagl --help` < 50 ms. | AOT gate in CI.
- **CLI-002 - Command framework, output modes, exit codes** (P0) | Parser, TTY detection, `--json`/`--quiet`/`NO_COLOR`, stderr progress, exit-code table §4. | CLI-001 | Each documented exit code reproducible; stdout holds only the result. | Unit per mode and code.
- **CLI-003 - HTTP client and error mapping** (P0) | Static `HttpClient`, problem-details by `code`, idempotency keys, retry with backoff for 5xx/network, `Retry-After`. | CLI-001, ARC-API-PREFIX | Matches desktop error semantics; no retry on 4xx except 429. | Unit with stub handler.
- **CLI-004 - Credential stores** (P0) | Windows Credential Manager, macOS Keychain, Linux libsecret via `LibraryImport`; env var precedence; refuse plain-file storage. | CLI-001 | Secrets never written to disk outside the OS store (verified by test that scans config dir); headless Linux prints env-var guidance. | Unit (fakes); per-OS integration in CI.
- **CLI-005 - `login`/`logout`/`whoami` via device flow** (P0) | Device flow with `--no-browser`; logout revokes then wipes; whoami shows plan and session. | CLI-004, BE-AUTH-DEVICE, FE-ACTIVATE, BE-AUTH-NATIVE | Works over SSH without a browser; logout leaves no credential entry. | Integration vs local API.
- **CLI-006 - Token refresh with cross-process lock** (P0) | In-process single-flight + OS-level lock, `refresh_raced` re-read. | CLI-005 | 10 parallel `zaagl ls` at token expiry never revoke the family. | Integration stress test.
- **CLI-007 - `link <url>`** (P0) | Link create, `--slug`, `--expires`. | CLI-003, CLI-006, BE-LINKS-SHORTURL, BE-AUTH-SCOPES | Prints absolute short URL; plan errors exit 4. | Integration.
- **CLI-008 - `paste` from stdin** (P0) | Streaming read with cap, ANSI strip, `--lang`, `--name`, `--tee`, works with `|` and `<` on cmd, PowerShell, bash, zsh (UTF-8 and UTF-16 PowerShell pipes). | CLI-007, BE-SNIPPET-CREATE, BE-CAPS | `dotnet test 2>&1 \| zaagl paste --lang log` returns a link; oversize exits 8 before upload completes. | Unit (encoding, ANSI); integration per shell (manual for cmd/PowerShell).
- **CLI-009 - `share <path>` with resumable upload** (P0) | Hand-written tus client, progress bar, Ctrl+C terminate, resume on transient failures within the run. | CLI-003, BE-UPLOAD | 1 GB file completes across a forced connection reset; Ctrl+C exits 130 and terminates the session. | Mock tus unit tests; integration.
- **CLI-010 - `ls` and `config`** (P1) | Listing with paging, table/JSON output; config file get/set/list/unset of non-secret keys. | CLI-002, BE-CONTENT-LIST | `ls --json` is valid JSON Lines; config rejects unknown keys and secret-looking values. | Unit; integration.
- **CLI-011 - API-key mode for CI** (P1) | `--with-api-key` (stdin) and `ZAAGL_API_KEY`; clear 402 messaging for free users. | CLI-004, BE-AUTH-SCOPES, ARC-FREE-CLI | Scoped key works for allowed commands, gets exit 3 with a scope message otherwise. | Integration with keys of each scope.
- **CLI-012 - Signing, notarisation, release artefacts** (P0) | Authenticode (Windows), codesign + notarise (macOS), SHA256SUMS + signature, GitHub Release automation. | CLI-001, ARC-SIGNING | Downloaded macOS binary runs without Gatekeeper block; checksums verify. | CI verify steps; manual first-run.
- **CLI-013 - Package managers** (P1) | winget `Zaagl.CLI`, Homebrew formula in tap, Linux tarball install script with checksum verification; Scoop/.deb later (P3). | CLI-012 | Install + `zaagl version` on each channel. | Manual per release; CI lint of manifests.
- **CLI-014 - Completions, update check, docs** (P2) | `completion` for 4 shells, daily cached version check (off in CI), `--help` text and a docs page. | CLI-002, BE-CLIENTVER | Completions work in bash/zsh/pwsh/fish; no network call when `CI` is set. | Unit; manual shells.
- **CLI-015 - `rm`, `open`, `--copy`** (P2) | Delete own share, open in browser, copy result to clipboard where supported. | CLI-010, BE-CONTENT-DELETE | Commands work on Win/mac; Linux `--copy` degrades with a message. | Integration; manual.

---

## 8. Open questions
1. **Shortcut default:** Cmd/Ctrl+Shift+Z is Redo almost everywhere; keep the PRD default (with warning) or ship Ctrl+Alt+Z / Cmd+Ctrl+Z? (Product)
2. **Windows signing:** EV on cloud HSM vs Azure Trusted Signing (eligibility, SmartScreen reputation behaviour today) - who owns procurement and budget (ARC-SIGNING)?
3. **Free-tier CLI/CI:** spec makes API keys premium-only; free users get interactive device sessions only. Acceptable, or a free read-only/limited key (ARC-FREE-CLI)? Should keys stop working after a downgrade (A12)?
4. **Multiple files / folders** on the clipboard or `share a b c`: one zip, a collection link, or one link per file? Needs backend "collection" decision.
5. **Headless Linux CLI** with no Secret Service: env-var only (current proposal) or an encrypted file with a user passphrase (conflicts with "OS store only" rule)?
6. **Built-in region screenshot** (needs macOS Screen Recording permission) in Phase 1 or later?
7. **Malware scan timing:** is the link usable ("processing" page) immediately after upload, or only after ClamAV passes? Affects "copy when done" semantics.
8. **Admins on native clients:** confirm native tokens never carry Admin role/TOTP privileges.
9. **Shared UI:** extract `@zaagl/ui` workspace package in the frontend repo, or copy into the desktop repo? Monorepo or separate `zaagl-desktop` / `zaagl-cli` repos?
10. **Telemetry/crash reporting:** opt-in only? Which service (self-hosted Sentry/GlitchTip?) and data policy, given the spec's privacy stance.
11. **Minimum OS versions:** Windows 10 1809+ and macOS 12+ proposed; Linux desktop explicitly out of scope for Phase 1?
12. **API prefix and versioning** for public clients (`/api` vs `/api/v1`, A17), and the minimum-client-version mechanism (BE-CLIENTVER).
13. **Session lifetime for native clients:** keep sliding 30-day refresh with no absolute cap (A3), or cap at e.g. 90 days and re-approve?
