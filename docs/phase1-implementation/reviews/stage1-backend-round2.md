# Stage 1 review: backend round 2 (PD-39/42/43/47 pages, F-09, PD-44, PD-41, refresh_raced)

Reviewer: zaagl-head. Date: 2026-11-09. Checkout `C:\Users\pc\Zaagl-Backend`, HEAD `9923526`, index empty, nothing committed. Read from the diff; no build or test run by the reviewer (the lead is running the full build and test on the final code).

## Verdicts

| Item | Verdict | Owner of follow-up |
|---|---|---|
| PD-39/42/43 short-host pages, `ResolveKind.Deleted/Disabled/ClickLimitReached`, `StatusPages.cs` | ACCEPT | – |
| F-09 `Cache-Control: private, max-age=0` on redirects, `no-store` on pages | ACCEPT | – |
| PD-47 HEAD on click-limited links (closes the RD-01 rework) | ACCEPT | – |
| PD-44 fallback URL blocklist + Web Risk | ACCEPT WITH NOTES (error codes) | backend |
| PD-41 one-time codes, social exchange, hub tickets | ACCEPT WITH NOTES (frontend must ship with it; remove `?access_token=`) | frontend, backend, devops |
| Migration `20261109140922_AddOneTimeCodes` | ACCEPT | – |
| Social sign-in refused for Admin accounts | ACCEPT (correct per spec §7.4) | – |
| `refresh_raced` → 409; `VectorGenTrace` writes to temp | ACCEPT (not inspected line by line) | – |

## Short-host pages (PD-39, PD-42, PD-43, PD-47, F-09)

- Redirects carry `Cache-Control: private, max-age=0` (spec L539); pages set `no-store`, CSP `default-src 'none'; style-src 'unsafe-inline'`, `nosniff`, `Referrer-Policy: no-referrer`, HEAD without body, no cookie.
- **Pages never echo the destination**: titles and messages are fixed strings, HTML-encoded; the only dynamic value is the report-page URL from configuration (`Links:ReportPageUrl`), accepted only if absolute http(s), encoded, `rel="noreferrer"`, default `https://app.zaa.gl/report`.
- Mapping matches the decisions: expired + fallback → 302 fallback, expired without fallback and deleted → 410 "expired", disabled → 410 "disabled", abuse/safety → acceptable-use page, click limit used up → 410 "click limit reached" and never the fallback (PD-43), purgeable/unknown → 404.
- PD-47: `ResolutionService` returns `ClickLimitReached` for any HEAD on a link with `MaxClicks` (before any lookup of the budget), so HEAD never shows the destination of a click-limited link and never consumes. Ordinary links: HEAD answers like GET without counting. The earlier RD-01 rework is closed.
- Note: an expired link that redirects to its fallback is counted as a redirect "miss" (30/min per address). Acceptable; mention it in RUNBOOK if owners report 429s.

## PD-44

- The fallback URL gets the blocklist check inside the create/edit transaction (`BlockedUrlErrorAsync`) and the Web Risk lookup with the **same fail-open rule as the destination** (`allChecked &= result.Checked`, re-checked later): saving a link is not blocked when Web Risk is down. Correct per spec.
- **Error-code naming (QA's question), recommendation:** keep separate codes `fallback-blocked` and `fallback-unsafe` next to `destination-blocked` / `destination-unsafe`. The UI must point at the right field, and QA can assert which URL was refused. Same kebab-case style as `insufficient-scope`. Not a user decision. Note: today these are a prefix inside a `validation` message; ARC-016/BE-028 should turn them into real problem `code` values for both pairs together.

## PD-41 one-time codes

- **Unguessable:** 32 random bytes from `RandomNumberGenerator` (256 bits), base64url (43 chars); malformed input rejected before hashing or touching the DB.
- **Stored hashed:** only `SHA-256(code)` (hex, primary key) plus purpose, user, created, expires. Lifetime 60 s; expired rows swept on issue.
- **Single-use under concurrency:** consumption is `DELETE … WHERE code_hash = $1 AND purpose = $2 AND expires_at > now` and succeeds only if exactly one row was deleted. Two concurrent consumers: PostgreSQL row locking makes the second DELETE re-check and delete 0 rows, so exactly one wins. Good.
- **Purpose separation:** every lookup filters on purpose (`ExternalLogin` vs `HubTicket`), so a hub ticket cannot be exchanged for a session and a sign-in code cannot open a hub connection. The hub-ticket scheme only applies to `/hubs/*` with `?ticket=`; its principal carries `auth_method=hub_ticket`, never API-key scopes. Negotiate only peeks; the WebSocket connect consumes, so one ticket opens one connection.
- **Minting:** `POST /api/auth/hub-ticket` is `Interactive` (JWT only; enrol-only tokens refused by AZ-01), rate-limited; `POST /api/auth/external/exchange` is anonymous, rate-limited `auth`, 401 `invalid_code` on any failure.
- **Admin refusal:** spec §7.4 (L560) makes TOTP mandatory for Admin; social sign-in has no second factor, so refusing it for Admin accounts is correct, not a new product rule.
- **Leaks:**
  - Serilog request logging logs the path, not the query string (no custom template), so `?code=`/`?ticket=` are not in app logs.
  - Nginx access logs would contain them: OPS-022 must redact `code` and `ticket` like grants.
  - Referer: the API's callback redirect sets `Referrer-Policy: no-referrer`. **The frontend has no Referrer-Policy anywhere** and has not been updated at all: `SocialCallback.tsx` still expects `#token=…`. **Social sign-in is broken until the frontend change lands; both must be committed together.** Frontend task: read `?code=`, call the BFF to exchange it server-side, remove it from the URL with `history.replaceState` at once, and send `Referrer-Policy: no-referrer` on `/auth/callback` (or app-wide `strict-origin-when-cross-origin` plus no-referrer there). The live-stats hook must switch to hub tickets the same way.
- **Must before Stage 2 (PD-41):** remove the deprecated `?access_token=` path for `/hubs` (`Program.cs:122`). The hub-ticket principal is not re-checked against the security stamp; with a 60 s single connection that is acceptable, but a disabled user's open hub connection stays open until it drops. Note for the backlog.

## Migration

`AddOneTimeCodes` only creates `identity.one_time_codes` and an index on `expires_at`: expand-only (spec MUST NOT #32). A plain index is fine on a new empty table. Column types, lengths and keys match the model (`IdentityDbContext`) and the model snapshot. No foreign key to users (codes live 60 s; the exchange refuses missing or disabled accounts). The agent applied it to the shared dev DB; record that in STATUS so other agents know the schema moved. CI `migrations-check` will prove "no pending model changes" on GitHub.

## Other

- `tests/Zaagl.Links.Tests/vectors.json`: if it still shows as modified (line endings only), the lead restores it before committing; `VectorGenTrace` now writes to temp, so it should not recur.
- Evidence (agent): build 0 errors; Links 390/390 on 2 of 3 runs, 4 shared-DB flakes on run 1 (`EmailJobsTests`, `AdminApiTests`) recorded per PD-24; QA 555/555 on a mid-edit snapshot. Final evidence = the lead's full run.

## New finding and question for the user

Pre-existing Phase 0 behaviour, same class as PD-47: on a GET, visitors whose User-Agent looks like a **bot** do not consume a click (`ResolutionService.cs:130`, spec §7.3 "bots never move click_count"), and **link-preview crawlers** of a link with Open Graph tags get a preview page whose refresh tag and link contain the destination (`ResolutionEndpoints.cs:105-106`). A User-Agent is trivially faked, so anyone can open a one-time or click-limited link as often as they like without using it up.

- **Q-R1. Should click-limited links refuse to reveal their destination to bots and preview crawlers too?** Options: (a) yes: like PD-47, a click-limited link answers bots and crawlers with the "click limit" status and no destination, and chat previews of such links show nothing; (b) bots and crawlers consume a click like people do; (c) keep today's behaviour. Recommendation: (a). It matches your PD-47 decision, and click limits are meant for limited sharing.
