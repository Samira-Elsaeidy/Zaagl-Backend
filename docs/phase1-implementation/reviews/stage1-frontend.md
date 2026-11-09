# Stage 1 review: Frontend (FE-001 docs part, FE-002, FE-027, FE-029)

Reviewer: zaagl-head. Date: 2026-11-09. Repo: `C:\Users\pc\Zaagl-Frontend`, branch `phase1/stage-1`, HEAD `985948d` (the user's commit). Nothing committed; this review changes no product files.

## Verdicts

| Task | Verdict |
|---|---|
| FE-029 BFF session (cookie crypto, CSRF, refresh, proxy) | ACCEPT WITH NOTES. The two token exceptions must be closed before the Stage 2 gate (see Q-F1) |
| FE-027 browser side (auth-transport, session-store, token-store removal, legacy key cleanup) | ACCEPT |
| FE-002 test tooling (Vitest, Testing Library, MSW, Playwright, axe) | ACCEPT WITH NOTES (E2E not run) |
| FE-001 (docs part only) | ACCEPT (`docs/frontend-phase1-notes.md`); the rest of FE-001 stays open |

## Scope, index, commits

- `git diff --cached` is **empty**: the briefly staged `token-store.ts` deletion was unstaged. HEAD unchanged at `985948d`.
- Changed files are all frontend-owned (`src/**`, `e2e/**`, configs, `package*.json`, `.env.example`, `docs/frontend-phase1-notes.md`). `.github/workflows/frontend-ci.yml` (devops), `.gitattributes` and `.graphifyignore` (graphify) are not from this agent. `.gitignore` change: check with the lead whether it is the frontend's (test output) or graphify's.

## Verified by the reviewer

- `npx tsc --noEmit`: exit 0.
- `npm test` (vitest run): 3 files, **29/29 passed**, exit 0.
- Not re-run: lint, `npm run build`, the live round trip against the API (agent evidence only), E2E (**NOT_TESTED**: no `E2E_BASE_URL`).

## Security review of the BFF code

**Cookie encryption (`src/server/bff/session-crypto.ts`): good.** AES-256-GCM, 12-byte random IV per seal, 16-byte tag checked, AAD binds version and key id, `WEB_SESSION_KEYS` = `id:base64url(32 bytes)` list, newest encrypts and all decrypt (rotation), wrong-length keys and duplicate ids fail startup without echoing key material, anything malformed or tampered unseals to `null` (signed out). Size cap 3800 bytes.

**Cookie flags (`cookies.ts`, `config.ts`): match spec §7.13 (L793).** `__Host-zaagl_session` HttpOnly, Secure, SameSite=Lax, Path=/, no Domain; `__Host-zaagl_csrf` readable by script (needed for double submit). Dev on plain http uses un-prefixed names without Secure; the switch is `NODE_ENV === "production"`, which `next build`/`next start` always set, so production cannot be weakened by a flag. The cookie expires with the refresh token.

**CSRF: good.** `timingSafeEqual` with a length check; cookie and `x-zaagl-csrf` header must match; with a session, the value must equal the one sealed in the session; requests whose `Sec-Fetch-Site` is anything but `same-origin` are refused (this blocks the same-site `usercontent.zaa.gl` and `staging` hosts, which is the PD-03 condition). Fresh CSRF token on every sign-in. Applied to login, social adopt, refresh, logout, access-token and every non-GET proxy call.

**Proxy (`handleProxy`, `apiPathFor`): no SSRF, auth routes blocked.** The target is always `apiBaseUrl + "/api/" + encoded segments` (host fixed by server config); segments that are empty, `.`, `..` or contain `/` or `\` are refused; `auth/login`, `auth/refresh`, `auth/logout`, `auth/external` are refused (404), so the proxy can never return raw tokens. Request headers are an allow-list (never Cookie or Authorization from the browser); `Set-Cookie` and CORS headers from the API are dropped; redirects are not followed (`redirect: "manual"`). Remaining `/api/auth/*` routes reachable through the proxy (register, confirm, reset, TOTP, me, logout-everywhere) return no tokens.

**Where a token reaches page script (both documented by the agent):**
1. Live stats: `POST /bff/session/access-token` gives a 15-minute access token to `useLiveStats.ts` for the SignalR hub on `api.zaa.gl`, and SignalR puts it in the WebSocket query string.
2. Social sign-in: the API redirects with `#token=…&refresh=…`; the page sends the pair to `/bff/session/social`, which rotates it at once (the exposed copy dies), but script sees it once.

Both conflict with PD-23 / addendum §4.3: "No access or refresh token is held by browser script … before any Phase 1 content renders". Fine for Stage 1; they must be closed before the Stage 2 gate, or the user accepts an exception (Q-F1). Fixes are backend work: a one-time code exchange for social login, and either short single-purpose hub tickets or SignalR through the app host.

## Findings (notes, owners)

1. **Client IP through the BFF (security, devops + frontend).** The BFF forwards the browser's `X-Forwarded-For` header to the API. If the API trusts the web container as a known proxy (BE-030 note), a browser could set its own `X-Forwarded-For` and pick its rate-limit partition, which defeats the PD-14 guest limits and the login limits. Safe only if the reverse proxy in front of `app.zaa.gl` **overwrites** `X-Forwarded-For` with the real client address (not appends) and the BFF forwards only that value. Required: OPS-010 sets `proxy_set_header X-Forwarded-For $remote_addr` (after real-IP from Cloudflare) for the app host, and QA adds a test (forged XFF through the BFF does not change the partition). Until then guest limits stay BLOCKED (addendum §11.6).
2. **Host header to the API (devops + backend).** The BFF calls `ZAAGL_INTERNAL_API_URL` and sends no `X-Forwarded-Host`; the API's host routing answers 421 for unknown hosts. Production must either map the internal host name in `Hosts:Api` or the BFF must send `X-Forwarded-Host: api.zaa.gl` (DEVOPS_PLAN §3.1 assumed the latter). Decide in OPS-007; the live test passed only because it ran on localhost.
3. **`refresh_raced` returned as 400 instead of 409 (backend).** The BFF tolerates both; backend should map it to 409 (spec). Small; give it to the backend fix agent.
4. **CSP nonces vs partial prerendering (FE-003).** Technical; the architect decides. Not a user question.
5. **Dependencies.** vitest 5.1.0, @vitejs/plugin-react 6.2.1, Testing Library (react 16.3.3, dom 10.4.2, jest-dom 7.0.1, user-event 14.6.8), jsdom 31.0.0, msw 3.0.4, @playwright/test 1.65.0, @axe-core/playwright 4.14.0: standard, dev-only, consistent with Node 24 in CI. `@types/node` ^20 → ^24 matches the CI Node version. `npm ci` must succeed on the committed lock file (first CI run is the proof).
6. **E2E:** `e2e/sign-in.spec.ts` exists but has never run. Add a CI job (with `npx playwright install --with-deps` and a running API) before the Stage 2 gate; enable `npm test` in `frontend-ci.yml` now (devops).

## Questions for the user

- **Q-F1. Two places still give a sign-in token to the web page's script: live statistics, and the moment after Google/GitHub sign-in.** You decided (PD-23) that no token may be readable by page script once Phase 1 content is shown. Options: (a) fix both before Stage 2's content goes live (backend adds a one-time sign-in code and a short "live stats ticket"; about a day of work); (b) accept them as a known exception for Phase 1. Recommendation: (a).
