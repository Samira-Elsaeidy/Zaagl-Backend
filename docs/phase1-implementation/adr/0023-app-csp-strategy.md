# ADR-0023: Content Security Policy for app.zaa.gl (FE-003)

Status: **APPROVED 2026-11-09 (PD-53, product owner): option (c), the mixed approach**, with the half-day spike first
and nonces everywhere as the fallback if the spike fails. Reworked after the Head's round-3 review
(`reviews/stage1-round3-architect.md`): viewer `connect-src`, configurable user-content origin, content library pages
moved to class N. Task FE-003 (FRONTEND_PLAN.md:207).
Related: PD-23 (BFF session before content), PD-41 (no token in page script), PD-03/PD-37/PD-45 (user bytes only on
`usercontent.zaa.gl`), addendum §4.2, §4.3, §4.5. Author: zaagl-architect.

## Context

1. **FE-003 as planned** asks for "nonce-based CSP for app and viewer routes" with
   `script-src 'self' 'nonce-…' 'strict-dynamic'` and an Emotion cache `nonce` (FRONTEND_PLAN.md:207).
2. **The app uses Partial Prerendering.** `next.config.ts:5` sets `cacheComponents: true` and `next.config.ts:6`
   `partialPrefetching: true`; the frontend agent reports `◐ Partial Prerender` routes in the build
   (`Zaagl-Frontend/docs/frontend-phase1-notes.md:32`).
3. **Next 16's own docs (bundled with the installed version)**, `node_modules/next/dist/docs/01-app/02-guides/content-security-policy.md`:
   - ":38 Every time a page is viewed, a fresh nonce should be generated. This means that you **must use dynamic
     rendering to add nonces**."
   - ":391 When you use nonces in your CSP, **all pages must be dynamically rendered**" and ":397 **Partial
     Prerendering (PPR) is incompatible** with nonce-based CSP since static shell scripts won't have access to the nonce".
   - ":456-464 Subresource Integrity (Experimental)": hashes of the JavaScript files are generated at build time and
     added as `integrity` attributes, "allowing you to maintain static generation while still having a strict CSP".
   - ":534-538 Limitations of SRI": experimental, App Router only, "**Build-time only: Cannot handle dynamically
     generated scripts**".
   - ":487": nonces and SRI can be combined on dynamically rendered routes.
   - ":42": development needs `'unsafe-eval'`; production does not.
4. **Emotion/MUI inserts `<style>` elements** at runtime and during SSR through `useServerInsertedHTML`
   (`Zaagl-Frontend/src/lib/mui-bridge.tsx:51-63`, `dangerouslySetInnerHTML` at `:60`). The cache is created without a
   `nonce` (`mui-bridge.tsx:30-34`), so a strict `style-src` would block MUI today.
5. **No `proxy.ts` or `middleware.ts` exists** in the frontend repo (checked `src/` and the repo root). If FE-003 adds
   `proxy.ts`, its `matcher` must exclude `/bff/`, or every BFF request body is buffered and cut at 10 MB
   (`frontend-phase1-notes.md:31`).
6. **Routes** (`Zaagl-Frontend/src/app/`): `(marketing)` (home), `(auth)` (`sign-in`, `register`), `(app)`
   (`links`, `analytics`, `files`, `paste`, `settings`), `auth/callback` (PD-41 one-time code), viewers `p/[slug]`,
   `i/[slug]`, `f/[slug]`, `unlock/[slug]`, `stats/[slug]`, `expired`, and the BFF route handlers under `bff/`.
   `/activate` (device approval, ARC-014) does not exist yet. No page currently reads `cookies()`, `headers()` or
   `connection()` (grep over `src/app`), so none of them is dynamic today; the session is read client-side through
   `/bff/session`.
7. **Connections the page makes:** the BFF on its own origin, and the SignalR statistics hub on the API host:
   `${API_URL}/hubs/analytics?ticket=…` (`src/features/analytics/useLiveStats.ts:15-16`, `API_URL` from
   `NEXT_PUBLIC_API_URL`, `src/lib/config.ts:4-5`). User media and previews load from `https://usercontent.zaa.gl`
   (addendum §4.1, §8.1). A PDF viewer (PD-31, proposed, not approved) would need a worker.
8. **Why CSP matters more in Phase 1:** the app origin renders untrusted filenames, snippet text, Markdown output and
   metadata (addendum §4.3). The BFF (PD-23) removes tokens from script, but an XSS on `app.zaa.gl` could still act as
   the user through the BFF with the CSRF token it can read. CSP is the second line after "render as data only".

Not verified (needs the FE-003 spike, see Decision step 1): whether, with `experimental.sri`, the **inline** scripts
Next emits in a prerendered shell (the RSC flight payload `self.__next_f.push(...)` and the bootstrap) are covered by
hashes or by `'strict-dynamic'`, or are blocked by `script-src 'self'`. The bundled doc's SRI example uses plain
`script-src 'self'` (`content-security-policy.md:494`) without explaining inline payload scripts, and ":538" says
dynamically generated scripts are not handled. This is the single fact the recommendation depends on.

## Options

| | Option | Keeps PPR | Script protection | Cost / risk |
|---|---|---|---|---|
| (a) | Nonces everywhere, every route dynamic (`await connection()`) | no | strongest, documented path | every page server-rendered per request; no static shell; higher latency and hosting cost (doc :401-406) |
| (b) | SRI (`experimental.sri`) + hash-based `script-src`, no nonces anywhere | yes | strong for external chunks; inline payload scripts unverified | experimental feature; fails if inline payload scripts are not covered |
| (c) | Nonces only on routes that render user content or handle credentials (made dynamic), SRI/hash CSP on the static rest | yes, for marketing and app shell | nonce + `'strict-dynamic'` where untrusted data renders; SRI elsewhere | two policies to maintain; depends on (b)'s spike for the static routes |
| (d) | `script-src 'self' 'unsafe-inline'` | yes | none against injected inline script | rejected |

## Decision (APPROVED, PD-53)

Adopt **option (c)**, gated by a short spike:

1. **Spike (FE-003, first step, half a day):** enable `experimental.sri` in a branch build, serve a PPR route with
   `script-src 'self' 'strict-dynamic' 'sha256-…'` / `'self'` only, and record in the frontend notes whether any inline
   script is blocked (browser console + a `securitypolicyviolation` listener). Outcome A: SRI covers the shell, proceed
   with (c). Outcome B: inline payload scripts are not covered; then the static routes fall back to option (a) for
   `(app)` too (the signed-in app shell becomes dynamic) and only `(marketing)` stays static with a CSP that has no
   user data to protect. Either outcome is recorded in this ADR before it is approved.

   **Spike result (FE-003, 2026-11-09, reported by the frontend agent; not re-run by the architect): outcome A, with
   one refinement.** Build-time hashes cover fully prerendered pages. Partly prerendered routes (`/links/[id]`,
   `/links/[id]/edit`, `/stats/[slug]`) stream inline scripts created per request, which hashes cannot cover, so class
   S also carries a per-request nonce (items 2 and 3). Result: 0 violations on 19 routes, in `en` and `ar`.
2. **Route classes (as built in FE-003).**
   - **Class N (nonce + `'strict-dynamic'`, dynamic):** `/p/*`, `/i/*`, `/f/*`, `/unlock/*`, `/auth/*` (callback),
     `/sign-in`, `/register`, `/activate`, `/files`, `/paste` (with their sub-routes). These render untrusted content or
     handle credentials and codes. The content library pages live in a `(library)` route group so they get the class
     N policy; their URLs are unchanged. They are in class N because they render filenames, titles and snippet text
     that may have been created through an API key or the CLI (and, for pastes, the snippet body itself); that is
     untrusted data even though the viewer is its owner. PD-53 names "content viewer, sign-in and activation pages";
     adding the library is the stricter reading of the same rule and is recorded for the user's information. Each
     page calls `await connection()` (no `export const dynamic` under `cacheComponents`, `frontend-phase1-notes.md:37`)
     and gets a fresh 128-bit nonce from `proxy.ts`.
   - **Class S (build hashes + per-request nonce, no `'strict-dynamic'`):** `(marketing)`, `(app)/links` (including
     `/links/[id]` and `/links/[id]/edit`), `(app)/analytics`, `(app)/settings`, `stats/*`, `expired`. PPR kept.
     Fully prerendered pages are covered by the build's script hashes and `'self'`; the per-request nonce covers the
     inline scripts that partly prerendered routes stream. These pages show only the owner's own URL-link titles and
     destinations, aggregate numbers and public social-preview images; no third-party content and no user bytes.
   - **Emotion nonce:** the Emotion caches (`createCache({ nonce })`, `mui-bridge.tsx`) and `useServerInsertedHTML`
     `<style nonce>` read the nonce from Next's internal `HeadManagerContext`. This is **not a public Next API**:
     upgrade risk. Every Next upgrade MUST re-run the CSP header snapshot test and the E2E violation listener on both
     classes before it ships.
   - **BFF route handlers `/bff/*`:** JSON only; `proxy.ts` matcher excludes them; they send
     `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` from the handler itself.
3. **Header strings (production, as built).** `<api>` = `https://api.zaa.gl`; `<uc>` = the user-content origin from
   configuration (`NEXT_PUBLIC_USERCONTENT_ORIGIN` on the frontend, the same value as the backend's
   `Content:UserContentBaseUrl`; production `https://usercontent.zaa.gl`, staging its own host). The policy builder
   reads `<uc>` and `<api>` from configuration, never hard-codes them. `{N}` is the per-request nonce.

   Class N:
   ```
   default-src 'self'; script-src 'nonce-{N}' 'strict-dynamic'; style-src 'self' 'nonce-{N}';
   style-src-attr 'unsafe-inline'; img-src 'self' data: blob: <uc>; media-src 'self' blob: <uc>; font-src 'self';
   connect-src 'self' <uc>; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-src 'none';
   frame-ancestors 'none'; form-action 'self'; upgrade-insecure-requests
   ```
   Class S:
   ```
   default-src 'self'; script-src 'self' 'nonce-{N}' {sha256 hashes emitted by the build};
   style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https: <uc>; media-src 'self' blob: <uc>;
   font-src 'self'; connect-src 'self' <api> wss://api.zaa.gl; worker-src 'self' blob:; object-src 'none';
   base-uri 'none'; frame-src 'none'; frame-ancestors 'none'; form-action 'self'; upgrade-insecure-requests
   ```
   Notes on the strings:
   - Class S has no `'strict-dynamic'`: with it, browsers ignore `'self'` and the hashes' host allow-list, which the
     prerendered pages rely on. Scripts load only from `'self'`, a build hash or the request's nonce.
   - `style-src-attr 'unsafe-inline'` in class N (deviation from the original proposal, accepted in the spike): MUI
     sets `style` attributes on elements. It allows inline `style="..."` attributes only; `<style>` elements still
     need the nonce, and no script is allowed by it.
   - `img-src https:` in class S (deviation, accepted in the spike): social-preview images of the owner's link
     destinations load from arbitrary HTTPS hosts. Class N does not have it; there, images come only from `'self'`,
     `data:`, `blob:` and `<uc>`.
   - `connect-src` in class S includes `<api>` and `wss://api.zaa.gl` only because the live-statistics hub is reached
     directly on the API host with a single-use ticket (`useLiveStats.ts:15-16`, PD-41). If the hub is later proxied
     through the BFF, drop both. Class N pages never open the hub.
   - `connect-src <uc>` in class N: the viewers `fetch` ticketed bytes from the user-content host for `Range`-part
     assembly of large downloads (ARC-008 §5) and for react-pdf (PD-31), and the library may fetch status thumbnails.
     The host answers those requests with `Access-Control-Allow-Origin` set to the app origin only for `Inline` and
     `Preview` tickets (ARC-008 §9); `OPTIONS` preflights are allowed by Nginx and by Cloudflare WAF rule C3
     (`design/ops-001-hosts-and-dns.md`). `img-src` and `media-src` include `<uc>` in both classes; `blob:` in
     `media-src` covers media assembled in the browser.
   - `style-src 'unsafe-inline'` in class S is an accepted trade-off (part of the approved option): Emotion's runtime
     styles on a prerendered shell cannot carry a nonce. Class S pages render no third-party content.
   - `worker-src 'self' blob:` is needed only if PD-31 (react-pdf) is approved; drop `blob:` until then.
   - `img-src data:` is for MUI icons and QR previews; `blob:` for client-generated QR/download previews.
   - Development adds `'unsafe-eval'` to `script-src` (doc :42) and the dev server origin to `connect-src`.
   - `ZAAGL_CSP_REPORT_ONLY` switches both policies to `Content-Security-Policy-Report-Only` (rollout, item 6).
   - **Reporting: required before production enforcement (not built yet).** Both policies gain
     `report-to csp; report-uri /bff/csp-report` and every response carries
     `Reporting-Endpoints: csp="/bff/csp-report"`. The `/bff/csp-report` route handler accepts
     `application/csp-report` and `application/reports+json`, is rate-limited, caps the body size, and logs without
     query strings (viewer URLs never contain `#k=` server-side, but `/auth/callback` has a one-time code in its
     query). The policies MUST NOT be enforced in production until this endpoint exists and has run through one
     Report-Only staging cycle.
4. **Other headers on every app response:** `X-Content-Type-Options: nosniff`, `Referrer-Policy:
   strict-origin-when-cross-origin` (viewers and `/auth/callback`: `no-referrer`, already set for the callback at
   `next.config.ts:7-18`), `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`,
   `Cross-Origin-Opener-Policy: same-origin`.
5. **Later hardening (not Phase 1 blocking):** `require-trusted-types-for 'script'` on class N once the viewer
   components are confirmed free of DOM sinks (react-markdown with raw HTML off, PD-09 open).
6. Rollout: ship each policy first as `Content-Security-Policy-Report-Only` (`ZAAGL_CSP_REPORT_ONLY`) for one staging
   cycle with `/bff/csp-report` live (item 3), then enforce. Production enforcement requires the reporting endpoint.

## Consequences

- Viewers, the content library, unlock, sign-in and activation pages are rendered per request (they already depend on request data once
  they read the link key flow and session); the marketing page and the signed-in shell keep PPR (Outcome A).
- Two policies, both carrying a per-request nonce, with a header snapshot test per class (FE-003 AC) and an E2E
  violation listener.
- The Emotion caches take the nonce from Next's internal `HeadManagerContext` (item 2): an upgrade risk that the
  snapshot test and violation listener must catch on every Next upgrade.
- `experimental.sri` is an experimental Next feature; a Next upgrade may change it. If it is removed, class S falls
  back to option (a).
- Satisfies addendum §4.3 "user content only as data" with a second barrier exactly where user data renders.

## Rejected options

- **(a) everywhere:** gives up PPR and static delivery of the app shell and marketing page with no security gain on
  pages that render no third-party content. Kept as the fallback if the spike fails.
- **(b) alone:** leaves the viewers, where untrusted content renders, on an experimental mechanism whose coverage of
  inline scripts is unverified, with `style-src 'unsafe-inline'`.
- **(d):** `'unsafe-inline'` scripts defeat CSP against XSS, which is the threat Phase 1 adds.

## Approval record

- 2026-11-09 FE-003 spike: outcome A with per-request nonces on class S (Decision step 1). The approved mixed
  approach stands; the fallback (nonces with `'strict-dynamic'` everywhere) was not needed. Items 2 and 3 describe
  what was built. Open before production: the `/bff/csp-report` endpoint (item 3).

- 2026-11-09, PD-53 APPROVED by the product owner: mixed approach, option (c); spike first; nonces everywhere if the
  spike fails. `style-src 'unsafe-inline'` on class S is part of the approved option (class S renders no
  third-party content).
- Not covered by the approval and recorded only: the content library in class N (stricter than the approved list)
  and the configurable `<uc>` origin (technical). If the user disagrees with the library placement, it moves back to
  class S with no other change.
