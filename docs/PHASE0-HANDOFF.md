# Zaagl Phase 0: handoff

Where things stand, what has **not** been verified, and what is missing. Written so work can continue from a fresh
checkout (or a fresh Claude Code session) without this conversation.

Repos: `Zaagl-Backend` (ASP.NET Core 10) and `Zaagl-Frontend` (Next.js 16). Specs: the PRD and tech-stack HTML
documents and the Phase 0 design (`zaagl-phase0.html`), which are not in these repos. Keep copies in `docs/`.

---

## 1. The most important gap: nothing has run against a real database

Docker was never running on the dev machine, so **no SQL has ever been executed**. Everything below compiles and
passes unit tests, but these are unproven until you run them:

- All four EF Core migrations (Identity, Billing, Links, Analytics).
- The hand-written analytics migration: partitioned `click_events`, `ensure_click_partitions()`, indexes.
- Every raw-SQL analytics query (`AnalyticsQueries`), including the `= ANY({0})` click-totals query and
  `date_trunc(..., 'UTC')`.
- The atomic click-limit `UPDATE` (`LinkResolver.TryConsumeClickAsync`).
- The status filter in `LinkService.ListAsync` (LINQ over the `Policy` navigation).
- HybridCache with Valkey as L2, the SignalR hub, rate limiting, the Stripe webhook.

**First thing to do:** `docker compose up -d`, `dotnet run --project src/Zaagl.Api` (Development applies migrations),
open `/scalar`, then register, create a link, open `/{slug}`, and watch the click appear in
`analytics.click_events`. Expect to fix a few SQL/EF translation issues on the first run.

Then add **integration tests** with Testcontainers (Postgres + Valkey): migrations apply, click-limit race
(many parallel requests, exactly N succeed), redirect + analytics write, plan limits, slug collisions.

## 2. Backend

### Done
Identity (email, Google/GitHub code, JWT, API keys), Links (random + custom slugs, edit, folders, tags, search,
status filter, CSV bulk import, Web Risk scan), Resolution (cached 302, crawler Open Graph previews), Access
(password, expiry, click limit, fallback URL), routing (device, country, A/B), Analytics (buffered writer,
partitioned table, account-wide and per-link overviews, SignalR feed, public stats), Billing (Stripe checkout,
portal, webhook, Free vs Starter limits), rate limiting, migrations, Docker Compose, 105 unit tests.

Endpoints added late for the frontend: `GET /api/analytics/overview` (account-wide), `PATCH /api/links/{id}`
(active / public stats), per-link click totals and `?status=` on `GET /api/links`, owner group on the SignalR hub.

### Missing / not built
- **Analytics aggregates**: hourly/daily statistics tables. Dashboards query raw events. Fine early; add before
  traffic grows.
- **Wolverine and the worker**: nothing uses `Zaagl.Worker` yet. The click writer is a hosted service in the API.
  No retention cleanup job (old partitions are never dropped), no expiry/cleanup jobs.
- **Valkey-backed rate limiting**: limits are in-memory per instance.
- **UTM breakdown**: the design shows a "UTM Parameters" tab. UTM values are not stored on click events.
- **Event list** ("View events" in the design): no endpoint.
- **Custom domains / branded domain** (design shows a domain dropdown): not in the backend. PRD puts it in Pro.
- **Report Abuse** endpoint and admin tooling (risk register item).
- **Downgrade edge case**: a Starter user who downgrades keeps links with passwords/click limits/rules, but editing
  such a link on Free is rejected by the plan check even if the gated fields are unchanged.
- **GeoIP**: MaxMind database path is optional and unconfigured; only the `CF-IPCountry` header gives countries.
- **Keys/secrets to configure**: `Stripe__SecretKey`, `Stripe__WebhookSecret`, `Stripe__StarterPriceId`,
  `WebRisk__ApiKey`, `Authentication__Google__*`, `Authentication__GitHub__*`, `Jwt__Key` (production),
  `Resolution__VisitorSalt` (production). Each feature switches itself off when its key is missing.
- Nginx, production Compose, CI (GitHub Actions), backups: none.
- **Phase 1–3 modules** are empty folders.
- Nothing of the browser extension (WXT) exists.

### Decisions worth remembering
Generated slugs are lowercase letters and digits only (Sqids, min length 6, alphabet must never change); slugs are
unique case-insensitively via `slug_key`. Redirects are 302 with `no-store`. Click limits are enforced in Postgres,
never the cache. `click_events` carries a denormalized `owner_id`. The analytics migration was regenerated once
(before it was ever applied) to add `owner_id`.

## 3. Frontend

### Done (builds, lints and typechecks clean)
Sign-in, register, social callback, app shell with sidebar and mobile drawer, link library (search, status chips,
pagination, stat cards), create/edit link form (slug availability, plan gating, password/expiry/click limit,
device/country/A-B rules, UTM builder, folders, tags, QR with colours and PNG/SVG download, social preview editor),
link analytics (range picker, KPIs, chart, tabbed breakdowns, live indicator, More menu: activate, public stats,
delete), account-wide Analytics page, Settings (plan and billing, API keys, sign out), password unlock page,
"This pigeon has flown" page, public stats page, branded 404, landing page.

### Not verified: the browser run was interrupted
A scripted browser test (Playwright-core + Chrome against a throwaway mock API) was started but **not completed**.
It confirmed sign-in redirect and error handling, redirect back via `?next`, the library table, 10 rows per page,
stat cards, and the sidebar plan label. Then the browser disconnected during the status-filter / search / paging
steps and the cause was not found: it could be the headless browser (an earlier disconnect was caused by missing
`--disable-gpu`) **or a real page problem such as a render loop**. Treat the library filter/search/pagination as
unverified, and investigate by running `npm run dev` and using those controls in a real browser. Everything after
that point in the test (create form, analytics, settings, unlock, mobile layout) has never been exercised.

Also unreviewed: visual match against `zaagl-phase0.html` (no side-by-side comparison was done), keyboard
navigation and screen-reader behaviour beyond what the markup intends, and `npm audit` output.

### Deliberate differences from the design
- No branded-domain dropdown (the domain is shown read-only).
- No "Burned" status; statuses are Active, Password, Expired, Limit reached, Inactive.
- The "Redirect uptime" card became "Top referrer, 7 days" (there is no uptime source).
- "Filter" and "View events" buttons and the "UTM Parameters" tab were dropped (backend gaps above).
- Added beyond the design: Live range, Custom date range, Countries/Cities/OS/Browsers tabs, UTM builder,
  Organize fieldset (title, folder, tags, public stats), password/expiry details, routing rules editor, sign-in
  and register pages, Settings, API keys, public stats page.
- "Scan destination for phishing" is shown as always on (the backend always scans).

### Missing
- **Tests**: no unit, component or E2E tests in the repo. The throwaway Playwright script and mock API are not
  committed; rebuild them or use real Postgres via Docker.
- Library: folder and tag filters, bulk CSV import screen, per-row delete.
- Arabic / English i18n (`messages/*.json` are empty stubs; UI is English only).
- Light theme (the design is dark only).
- The session token lives in `localStorage` (simple, but readable by any XSS). A BFF with an httpOnly cookie is the
  hardening step.
- Phase 1–3 screens (`/files`, `/paste/new`, `/p`, `/f`, `/i`) are empty placeholders from the scaffold.
- Browser extension (WXT) is not started.

### Environment notes
- Copy `.env.example` to `.env.local`. `NEXT_PUBLIC_API_URL` defaults to `http://localhost:5084`.
- `NEXT_PUBLIC_SOCIAL_LOGINS=google,github` only after those providers are configured on the API.
- Next.js 16 with `cacheComponents` is on: anything reading the path, params or search params in the browser must
  sit under `<Suspense>` (see `(app)/layout.tsx`). Read `node_modules/next/dist/docs/` before changing routing.
- On the dev machine npm's configured cache (`F:\npm-cache`) is not writable; use `--cache <dir>` for installs.
- Headless Chrome needs `--disable-gpu` on that machine.

## 4. Suggested order for the next session
1. Start Docker, run the backend, fix whatever the first real run breaks (section 1).
2. Run the frontend (`npm run dev`) against the real API and click through every screen; fix the library
   filter/search/pagination problem if it is real.
3. Add Testcontainers integration tests, then a Playwright suite against the real stack.
4. Configure Stripe test keys and run a real checkout; configure Web Risk; test social sign-in.
5. Remaining gaps: aggregates, Wolverine jobs, retention, browser extension, Report Abuse.
6. Then Phase 1 (snippets, files, media, CLI, desktop app).
