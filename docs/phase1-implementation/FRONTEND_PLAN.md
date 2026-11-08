# Phase 1 frontend plan

| | |
|---|---|
| Owner | zaagl-frontend |
| Status | PLANNING, awaiting approval. No Phase 1 code exists yet. |
| Source | _drafts/frontend.md (whole), written 2026-11-09 by the planning agent named above and kept verbatim below this header |
| Decisions | Numbered PD-nn in [DECISIONS.md](DECISIONS.md). Draft question ids (D-n, Qn, Q-nn) map to PD entries there. |

## Consolidation notes

These are the lead architect's overrides. Where they differ from the draft text below, they win.

- **Viewer host (PD-02, proposed):** viewers are pages on `app.zaa.gl` (`/p`, `/i`, `/f`), reached by a 302 from `zaa.gl/<code>`. Where the draft describes viewers served on `zaa.gl` through Nginx path routing, that option is not taken.
- **Session (PD-23, proposed):** FE-029 (the full BFF cookie session of spec §7.13) is added to Stage 1 and must ship before any Phase 1 content renders in the app. FE-027 becomes its first step.
- **Markdown (PD-09):** open; the recommendation is the draft's `react-markdown` with raw HTML disabled.
- **Uploader:** `tus-js-client` with our own MUI UI instead of Uppy, recorded as a tech-stack deviation in DECISIONS.md.
- **Next.js 16 behaviour:** the draft's notes on `proxy.ts`, body limits and CSP nonces come from prior knowledge, because `node_modules` was not installed. FE-001 verifies them against `node_modules/next/dist/docs/` before code relies on them.
- The draft's open questions are mapped in DECISIONS.md (FE Q1–Q13).

---

# Phase 1 (micro-sharing) — Frontend plan (DRAFT)

Owner: zaagl-frontend agent · Scope: E1.1 snippets, E1.2 image/media, E1.3 file transfer · Repo: `C:\Users\pc\Zaagl-Frontend` · Status: planning only, nothing implemented.
Sources read: PRD (`src/Doc/zaagl-prd.html` §Phase 1, §6 tiers), tech stack (`src/Doc/zaagl-tech-stack (2).html` §04, §06, §07, §09, §14), spec v9.4 §1.2, §4.5 rule 6, §7.13 (`Zaagl-Backend/docs/spec/zaagl-spec-v9.4.md:50-59, 292, 777-796`), `Zaagl-Backend/docs/PHASE0-HANDOFF.md:101-118`.
Placeholder ids: `BE-xxx(<API group>)` = backend work from §5; `ARC-xxx(<topic>)` = architecture/product decision from §9; `OPS-xxx` = devops.

---

## 1. Audit findings

### 1.1 Facts about the current codebase
| # | Finding | Evidence |
|---|---|---|
| A1 | **`node_modules` is not installed**, so `node_modules/next/dist/docs/` could not be read (AGENTS.md requires it). Next.js 16 statements below are from prior knowledge and are marked *verify*; FE-001 re-checks them after install. | `Zaagl-Frontend/` has no `node_modules` |
| A2 | Phase 1 routes are 3-line placeholders; the 9 feature files are **0 bytes**. | `app/(app)/files/page.tsx:1-3`, `app/(app)/paste/new/page.tsx:1-3`, `app/p/[slug]/page.tsx:1-3`, `app/f/[slug]/page.tsx:1-3`, `app/i/[slug]/page.tsx:1-3`; `features/snippets/{SnippetEditor,SnippetViewer,DiffView,VersionList,LanguagePicker}.tsx`, `features/files/{Uploader,FileViewer,PdfPreview}.tsx`, `features/media/ImageViewer.tsx` all empty |
| A3 | Nav lists Phase 1 items as disabled "Coming soon" with no `href`. | `components/app-shell.tsx:47-56` (NAV), `:58-69` (UPCOMING), `:168-175` (disabled buttons) |
| A4 | Auth pattern: client-only shell redirects to sign-in when token is `null`; each layer behind `<Suspense>` (cacheComponents). | `app/(app)/layout.tsx:7-13`, `components/app-shell.tsx:232-236` |
| A5 | API client: `fetch` + `Authorization: Bearer` from localStorage, single-flight refresh, ProblemDetails mapping, bilingual fallback errors. Accepts `FormData` but **`fetch` exposes no upload progress** and buffers nothing resumable. | `lib/api-client.ts:70-117` (`:75-76` header, `:83` form body, `:96-99` refresh), `:44-68` |
| A6 | **Tokens live in `localStorage`** (`zaagl.token`, `zaagl.refresh`). Consequences for Phase 1: (a) `<img>`, `<video>`, `<a download>`, pdf.js range requests **cannot carry a Bearer header**, so owner-only media needs short-lived signed URLs; (b) any script injection on the app origin steals a 15-min access token **and the refresh token**; Phase 1 multiplies untrusted-content surfaces (SVG, Markdown, filenames, PDFs). Spec §7.13 requires a BFF + `__Host-` HttpOnly cookie and "no CORS"; Phase 0 diverges (handoff flags it). | `lib/token-store.ts:5-6, 9-15, 33-41`; spec `:793-795`; backend `Program.cs:63-67` (`AllowAnyHeader/AllowAnyMethod/AllowCredentials`), `:236`; `PHASE0-HANDOFF.md:107-108` |
| A7 | API prefix in code is `/api/...`, spec says `/api/v1/*`. This plan uses `/api/...` to match what exists; reconcile in BE contract. | `features/links/api.ts:23`; backend `LinksEndpoints.cs:24`; spec `:54` |
| A8 | Design system is **MUI 9 + Emotion** (stock palette, RTL via a second Emotion cache with `stylis-plugin-rtl`). **Tailwind 4 is installed and used lightly** (~54 `className` usages: layout utilities, `font-mono`, `font-arabic`, `rtl:rotate-180`, tokens in `globals.css`). No shadcn/ui despite the tech-stack doc. New Phase 1 UI: MUI + `sx`; Tailwind only for the existing token classes. | `lib/mui-bridge.tsx:28-35, 73-74, 96`; `app/globals.css:1, 4-31, 37-61`; e.g. `features/links/LinkForm.tsx:234, 374`; tech stack §07 "Tailwind CSS + shadcn/ui" |
| A9 | i18n: client-only `t()` with en/ar JSON (351 keys each, handoff note "empty stubs" is stale); locale read from localStorage **after** hydration, so SSR always renders English then flips to Arabic (visible flash on public viewers). | `lib/i18n/context.tsx:42-57, 68-72`; `app/layout.tsx:36` (`lang="en"`) |
| A10 | Formatters hard-code `en-US` (numbers, dates, country names); no `formatBytes`. | `lib/format.ts:3, 8, 12, 29` |
| A11 | Theme is client-only, defaults to dark on server, set via `data-theme` on `<html>`; tokens switch in CSS. Good fit for Shiki dual themes via CSS variables. | `lib/theme.tsx:26-37`; `app/globals.css:37` |
| A12 | `next.config.ts` has `cacheComponents`, `partialPrefetching`, Turbopack CSS rule; **no `headers()`, no CSP, no `assetPrefix`, no `proxy.ts`**. Emotion cache has no CSP nonce. | `next.config.ts:3-15`; `lib/mui-bridge.tsx:30-34, 57-61` |
| A13 | No test framework, no test scripts. | `package.json` scripts (`dev/build/start/lint` only); `PHASE0-HANDOFF.md:102-103` |
| A14 | Plan model knows only `tier: 0|1` (Free/Starter) and link limits; PRD Phase 1 needs Pro and per-tier file size/retention (Free 25 MB/7 d, Starter 100 MB/30 d, Pro 5 GB/permanent); spec §1.1 uses Guest/Free/Premium. | `lib/types.ts:118-129`; PRD §6 "Code, media & file sharing"; spec `:20-32` |
| A15 | Page `metadata` titles are hard-coded English (fine for Phase 0, but viewer `generateMetadata` must not leak gated content). | `app/(app)/links/page.tsx:4`, `app/unlock/[slug]/page.tsx:6` |

### 1.2 Reusable components and patterns
- **Public page pattern**: server page + `<Suspense>` + client view that `use(params)` — `app/stats/[slug]/page.tsx:7-14`, `features/analytics/PublicStatsView.tsx:16-18`. Reuse for viewers.
- **BrandedCard** (centered card + QuickToggles) for gates/expired/burned: `components/branded-card.tsx:11-45`; `app/expired/page.tsx`.
- **UnlockForm** (password gate, 401/410 handling): `features/links/UnlockForm.tsx:15-79` — refactor into a generic `PasswordGate` with `onUnlocked(payload)` instead of `window.location.assign` (`:33`).
- **QrPreview** takes only a `slug`: `features/links/QrPreview.tsx:27` — reusable as-is on snippet/file result panels. Move its `download()` helper (`:17-24`, revokes the object URL synchronously; delay revoke) and `copyText` (`features/links/api.ts:133-140`) to `lib/browser.ts`.
- **LinkForm** is a 495-line monolith; extract `AccessPolicyFields` (password/expiry/max — `LinkForm.tsx:80-85, 109-180, 340-418`), `OrganizeFields` (`:420-466`) and the sticky result/QR card (`:486-488`) so `/paste/new` and the uploader share them.
- **Controls**: `Button/ButtonLink/IconButton/Input/Select/Field/CheckRow/Banner/Card/PlanBadge` (`components/ui/controls.tsx:52-244`; `Field` a11y wiring `:148-178`), `ConfirmDialog` (`components/ui/confirm-dialog.tsx:12-47`, always "danger" — add a `tone` prop for burn confirm), `CollapsibleCard` (`components/ui/collapsible-card.tsx:18`), `useToast` (`components/ui/toast.tsx:52`), `StatusChip` (`components/ui/status-chip.tsx:5-11`, extend with `scanning/burned/processing`), icon wrapper `components/ui/icons.tsx:30-34`.
- **Library table**: `features/links/LinksView.tsx:75-85` (local filter state), `:185-255` (MUI table + skeleton rows), `:259-261` (empty states), `:272` (pagination); `useDebounced` (`lib/hooks.ts:5`); `queryString` (`features/links/api.ts:10-17`, move to `lib/`).
- **Live updates**: SignalR hook `features/analytics/useLiveStats.ts` — reuse for upload status (`scanning → ready`).
- **Query defaults** (no retry on 4xx): `lib/query-client.ts:10-13`.
- **Fonts**: IBM Plex Mono already loaded (`app/layout.tsx:13-18`, `--font-mono` `globals.css:25`) — use for code.

### 1.3 Risks
| Risk | Impact | Mitigation (task) |
|---|---|---|
| R1 localStorage tokens + new untrusted content | XSS → account takeover | Never render user HTML/SVG on app or viewer origin; CSP everywhere (FE-003); viewers on a credential-free origin; push ARC-xxx(BFF) before Phase 1 GA (FE-027) |
| R2 User content in `zaa.gl` origin (spec §4.5 r6) | Same-site with `app.zaa.gl` (spec `:794`) | Raw bytes only from a **separate registrable content domain** with `CSP: sandbox` / `attachment`; the Next viewer shell on zaa.gl renders text via React only (FE-009/015/017) |
| R3 Media/downloads need auth but tags can't send headers | Broken owner previews, or tokens in URLs | API-minted short-lived signed URLs (§5 API-R4, API-V5) |
| R4 Large uploads via Next/Cloudflare | Cloudflare Free 100 MB/request; Next proxy buffers request bodies (~10 MB default, *verify* `proxyClientMaxBodySize`), Server Actions 1 MB default (*verify*) | Uploads go **browser → tus → DNS-only files host**; Next never touches file bytes (FE-013) |
| R5 Bundle bloat (CodeMirror, pdf.js, hljs, Shiki) | Slow viewer pages | Server-side Shiki/Markdown; `next/dynamic` per route; budgets (FE-023) |
| R6 Burn-after-read consumed by link unfurlers (Slack/WhatsApp GET) | Recipient gets "already burned" | GET never consumes; consumption is an explicit POST on button click (FE-012/017) |
| R7 Filename spoofing (`U+202E`, `invoice<U+202E>fdp.exe` renders as "invoiceexe.pdf") | Malware disguise | Strip bidi controls, show extension in its own chip, MIME from server (FE-017) |
| R8 SSR English → Arabic flash, theme flash | UX on public pages | Accept for Phase 1 or ARC-xxx(locale cookie — not allowed on zaa.gl) → use `Accept-Language` on SSR for viewers (FE-021) |
| R9 `next/image` optimizer re-encodes | Violates "100% original resolution and colour" | Plain `<img>`; never `next/image` for user media (FE-015) |

---

## 2. Routes, pages, hosts, layouts, states

**Hosts** (proposal, needs ARC-xxx(hosts) + OPS-xxx(nginx)):
- `app.zaa.gl` — dashboard (authenticated, existing `(app)` group).
- `zaa.gl/p|i|f/<slug>` — viewer pages served by the **same Next app**, Nginx path-routes `^/(p|i|f)/` on `zaa.gl` to `zaagl-web` (1-char multi-segment paths are reserved, spec §4.5 r6). Set `assetPrefix` to `https://app.zaa.gl` (or a static host) so `zaa.gl` never needs `/_next/*`. Next must set **no cookies** on these routes (spec §1.2). Viewer origin holds no tokens (localStorage is per-origin).
- `<content-domain>` (e.g. `zaaglusercontent.net`, separate registrable domain) — raw bytes: images, video, SVG, PDF, raw text, downloads, via Nginx `X-Accel-Redirect` after API authorisation. Headers: `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox`, `Content-Disposition: attachment` for everything except image/video/pdf/text-plain previews, `Cross-Origin-Resource-Policy: cross-origin`, CORS `GET` from `https://zaa.gl` and `https://app.zaa.gl` only (pdf.js, Copy Raw).
- `<files-upload-host>` (DNS-only, tech stack §09) — tus endpoint; CORS for `https://app.zaa.gl` with `Upload-*`, `Tus-Resumable`, `Authorization` headers.
- Short code `zaa.gl/<code>` of a snippet/image/file: API resolves and `302`s to `/p|i|f/<code>` (BE-xxx(resolve)).

| Route | Host | Purpose | Rendering |
|---|---|---|---|
| `(app)/paste/new` | app | Snippet editor + options + result card | client; CodeMirror via `next/dynamic({ssr:false})` |
| `(app)/paste/[id]` | app | Owner view: preview, versions, "new version", analytics (reuse `LinkAnalyticsView` parts), delete | client |
| `(app)/files` | app | Files & media library (grid/list, kind tabs `all/image/video/file`, quota bar) + inline drop zone | client; filters in URL search params under `<Suspense>` |
| `(app)/files/new` | app | Full-page uploader (multi-file queue, options per batch) | client |
| `(app)/files/[id]` | app | Owner detail: preview, link settings, download (signed URL), analytics, delete | client |
| `(app)/links` | app | Existing library gains a **Type** column + kind filter (all short links share one engine) | existing |
| `(viewer)/p/[slug]` (+ `?v=n`, `?diff=a..b`) | zaa.gl | Snippet viewer | **Server Component** fetch (internal API) + server Shiki; client islands for copy/wrap/gate |
| `(viewer)/i/[slug]` | zaa.gl | Image/GIF/SVG/video viewer | server shell + client `ImageViewer`/`VideoPlayer` |
| `(viewer)/f/[slug]` | zaa.gl | Download page (+ PDF preview, burn confirm) | server shell + client actions; `PdfPreview` lazy |
| `(viewer)/p/[slug]/unlock` etc. | — | **not separate routes**: gate is inline (state machine in the page) to keep the grant in memory | — |
| `app/api/view/[slug]/…` route handlers | zaa.gl | Thin same-origin JSON proxy to the internal API for unlock/reveal/download-grant/raw (removes CORS from viewers; small bodies only; never file bytes; never sets cookies) | Route Handlers |

Layouts: move `p/f/i` into a `(viewer)` route group with `ViewerLayout` (thin top bar: brand → marketing, `QuickToggles`, "Share your own" CTA; footer: "Report abuse" (spec §7.15), "Expires in …"). No `AppShell`, no `useToken`. Each viewer page: `loading.tsx` (skeleton matching kind), `error.tsx` (Banner + retry, bilingual), `not-found.tsx` (reuse `NotFoundBody`). Content states (from API `state`): `active`, `gated` → PasswordGate in `BrandedCard`, `reveal` (burn-after-read) → interstitial, `scanning/processing` → "Your file is being checked" with auto-refresh, `expired/burned` → reuse `/expired` body with "burned" copy, `blocked` (abuse) → neutral notice, `rejected` (malware/type mismatch, owner only). Dashboard pages: `PageSkeleton` (`app-shell.tsx:200-212`), empty states as `LinksView.tsx:259-261`, errors via `Banner` + retry.
cacheComponents: viewer metadata is **never cached** (revocation/burn must be immediate); highlighted HTML of an immutable version **may** use `'use cache'` keyed on `(contentHash, lang, version)` (*verify* `cacheLife`/`cacheTag` API in bundled docs).

---

## 3. Components

**3.1 SnippetEditor** (`features/snippets/SnippetEditor.tsx`): `@uiw/react-codemirror` loaded with `next/dynamic` only on `/paste/new` and "new version"; extensions: line numbers, bracket matching, `EditorView.lineWrapping` toggle, `@codemirror/language-data` for **lazy per-language** modes, theme built from MUI palette via `EditorView.theme` (no theme package). `indentWithTab` **off** by default (keyboard trap) with a visible "Tab inserts indentation" toggle; editor container `dir="ltr"` even in Arabic UI. Paste/drop of a text file fills the editor and filename. Limits shown live (bytes/lines vs plan). Kind selector: `code | text | markdown`. Fallback when JS chunk fails: plain `<textarea>` (same form).
**3.2 LanguagePicker + detection** (`LanguagePicker.tsx`, `lib/languages.ts`): MUI `Autocomplete` over a registry mapping Shiki id ↔ highlight.js id ↔ file extensions ↔ display name (100+ entries from Shiki's bundled list). Detection order (tech stack §14): **1 user's explicit pick → 2 filename extension → 3 highlight.js `highlightAuto` restricted to the registry subset (debounced 500 ms, only when ≥ 3 lines, relevance threshold, run in `requestIdleCallback`) → 4 `plaintext`**. Detection runs at creation time only; the chosen language is saved, so viewers never load highlight.js. Show "Detected: TypeScript (change)".
**3.3 SnippetViewer** (`SnippetViewer.tsx` + `lib/highlight/`): **server-side Shiki** in the Server Component using `codeToHast` → React via `hast-util-to-jsx-runtime` (**no `dangerouslySetInnerHTML`**), dual themes (`github-light-default`/`github-dark-default`, `defaultColor: false`) switched by `[data-theme]` CSS, so zero highlighting JS ships. Client Shiki (`shiki/core` + JavaScript regex engine, grammars imported on demand) only for content that is unlocked client-side (password/burn) and for the editor preview. Line numbers via CSS counters (not copied with selection); line anchors `#L12-L20` with highlight; wrap toggle; Copy Raw (`navigator.clipboard`, from in-memory text; for > 1 MB fetch raw from content domain), "Raw" link (content-domain `text/plain`, sandboxed), Download (`.ext` from language). Guard: > 512 KB or > 20 k lines → render plain monospace (no highlight) with notice. Code area `dir="ltr"` + `unicode-bidi: isolate`; plain text and Markdown use `dir="auto"` (Arabic notes).
**3.4 Markdown** (`features/snippets/MarkdownView.tsx`): recommended **render in the Server Component with `react-markdown` + `remark-gfm`**, raw HTML **disabled** (`skipHtml`, no `rehype-raw`), `urlTransform` allowing only `http/https/mailto` and in-page `#`, links `rel="nofollow noopener noreferrer ugc" target="_blank"`, **remote images rendered as links** (tracking pixels; CSP `img-src` blocks them anyway), fenced code highlighted by the same Shiki module. Toggle "Rendered / Source". Alternative per tech stack: backend Markdig + HtmlSanitizer returns HTML — then the frontend must still pass it through DOMPurify (`isomorphic-dompurify`) before `dangerouslySetInnerHTML`; double sanitisation and a second parser, so not preferred (ARC-xxx(markdown)).
**3.5 DiffView + VersionList** (`DiffView.tsx`, `VersionList.tsx`): recommended: render **server-computed hunks** (DiffPlex, BE) with our own MUI component — side-by-side ≥ `md`, unified below; each line highlighted with Shiki; `+`/`−` gutter glyphs and SR text (not colour-only); collapse unchanged regions. Fallback option: `react-diff-viewer-continued` (stack choice) — brings `@emotion/css` (second Emotion runtime outside our RTL cache) and its own `diff`; verify React 19 peer. VersionList: MUI list `v3 · 2 h ago · +12 −4`, select two to diff, URL `?diff=2..3`.
**3.6 ImageViewer** (`features/media/ImageViewer.tsx`): plain `<img src={signedUrl} decoding="async">` of the **original bytes** (never `next/image`; no CSS filters), intrinsic size from API to reserve layout (no CLS). Background switcher (MUI `ToggleButtonGroup`): dark / light / checkerboard (`repeating-conic-gradient`), remembered in localStorage per viewer. Fit / 100% / zoom in/out buttons, wheel+ctrl zoom, drag pan, keyboard (`+ − 0 1 arrows`). **Loupe**: circular overlay `div` using the same URL as `background-image` with `background-size = natural × factor` positioned at pointer (no canvas, so no CORS), toggle `L`, factor 2–8×, `image-rendering: pixelated` option above 400%; disabled on `(pointer: coarse)` → tap-to-zoom + native pinch. Metadata panel: dimensions, type, size, colour profile flag if API gives it. GIF: animated `<img>`; with `prefers-reduced-motion` show poster (static first frame from worker) + "Play".
**3.7 SVG safe display**: **only** as `<img src>` (scripts and external loads never run in image context) pointing to the content domain; never inline, never `dangerouslySetInnerHTML`, never `<object>/<embed>`/un-sandboxed iframe. Content domain serves SVG with `CSP: sandbox; default-src 'none'; style-src 'unsafe-inline'` so opening the raw URL can't execute. Optional worker PNG rasterisation for thumbnails (BE).
**3.8 VideoPlayer** (`features/media/VideoPlayer.tsx`): native `<video controls preload="metadata" playsInline poster>` with sources `[transcoded mp4 (H.264/AAC), original]`; Range requests via Nginx; no library. Keyboard via native controls; label from title; download button only if owner allows.
**3.9 Uploader** (`features/files/Uploader.tsx`, `useUploadQueue.ts`): `react-dropzone` (drop zone + accessible "Choose files" button + `paste` of clipboard images/screenshots) → per file: `POST /api/uploads` (quota/size check, gets tus endpoint + **per-upload token**) → `tus-js-client` `Upload` with `chunkSize` 50 MB (< Cloudflare limit even if proxied), `retryDelays [0,1s,3s,10s,30s]`, `onBeforeRequest` sets `Authorization: Bearer <uploadToken>` (never the account token on the DNS-only host), `onShouldRetry` refreshes the token via `POST /api/uploads/{id}/token` on 401; `findPreviousUploads()` for resume after reload/network loss. UI: MUI list with `LinearProgress variant="determinate"` (role=progressbar, aria-valuenow), speed/ETA, pause/resume/cancel, `aria-live="polite"` milestones (25/50/75/100 %, "Scanning…", "Ready"). After 100 %: status via SignalR (`useLiveStats` pattern) with polling fallback `GET /api/uploads/{id}` until `ready | rejected`. Client pre-checks (size vs plan, max files) are UX only; server is authoritative. Options before upload: expiry days (plan-capped), Burn After Downloading, password, custom slug (reuse `AccessPolicyFields`). `beforeunload` warning while uploading.
**3.10 FileViewer / download page** (`features/files/FileViewer.tsx`): server shell renders filename (bidi-stripped, extension chip), size (`formatBytes`, locale-aware), type icon, expiry, scan status "Scanned for malware". Download button → `POST` (same-origin route handler → API) returns one-time URL → `window.location.assign(url)` (content domain, `attachment`). **Burn After Downloading**: confirm dialog (`ConfirmDialog` with `tone="warning"`): "This file will be deleted after this download. Make sure your connection is stable and save it now." Only after confirm does the POST consume; afterwards page shows "This pigeon has flown". Multi-download plans show remaining count. Image/video files link to `/i/` preview; PDF shows `PdfPreview`; ZIP shows filename + size only (no listing in Phase 1).
**3.11 PdfPreview** (`features/files/PdfPreview.tsx`): `react-pdf` via `next/dynamic` only when `mime === application/pdf`; pdfjs worker self-hosted from our static assets (CSP `worker-src 'self'`), `isEvalSupported: false` (CVE-2024-4367), no XFA, no annotation links auto-follow (render with `rel=noopener`), page-by-page lazy render, max first 20 pages then "Download to view all". Fetches the PDF from the content domain with CORS + Range. Preferred alternative if BE agrees: worker renders first N pages to PNG (libvips/pdfium) → plain `<img>` (zero bundle, no pdf.js attack surface) (ARC-xxx(pdf preview)).

---

## 4. New dependencies

| Package | Why | Where loaded | Approx. gz cost (measure in FE-023) | Reuse/alternative |
|---|---|---|---|---|
| `shiki` | Highlighting 100+ langs (stack choice) | **Server** (0 KB client); client lazily for unlocked content | client ≈ 60–80 KB + grammar on demand | — |
| `hast-util-to-jsx-runtime` | Shiki/Markdown hast → React without `dangerouslySetInnerHTML` | server | ~5 KB | already a transitive dep of `react-markdown` |
| `@uiw/react-codemirror`, `@codemirror/language-data` | Editor (stack choice; Monaco rejected) | `/paste/new` only, dynamic | ~130–160 KB + per-language 5–30 KB | `<textarea>` fallback |
| `highlight.js` (import `highlight.js/lib/core` + registered subset) | Auto-detect language | `/paste/new` only, dynamic | ~60–90 KB for ~40 langs (full build ~1 MB: forbidden) | skip detection = extension + plaintext |
| `react-markdown`, `remark-gfm` | Safe Markdown → React | server | 0 client | BE Markdig + `isomorphic-dompurify` (§3.4) |
| `tus-js-client` | Resumable uploads | upload pages | ~20 KB | `@uppy/core` + `@uppy/tus` (stack choice; wraps tus-js-client, adds ~40 KB; Uppy UI packages not MUI → not used). Recommend tus-js-client directly with MUI UI |
| `react-dropzone` | Accessible drop zone, file dialog quirks | upload pages | ~9 KB | native DnD events (more a11y work) |
| `react-pdf` (+ `pdfjs-dist` peer) | PDF preview | `/f/` PDFs only, dynamic | ~350 KB + worker ~300 KB | server-rendered page PNGs (§3.11) |
| `react-diff-viewer-continued` | Diff (stack choice) | **not recommended** | ~35 KB + `@emotion/css` + `diff` | own `DiffView` over BE DiffPlex hunks (no dep) |
| `qrcode.react` | already installed (`package.json`) | result cards | 0 new | reuse `QrPreview` |
Not needed: `axios`, React Hook Form, Zod, shadcn (stack doc) — existing `api()` + controlled MUI forms are the established pattern; adding them violates "no new state/design system". Dev deps in §7.

---

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

## 6. i18n / RTL, accessibility, responsive

- **i18n**: new namespaces `snippet.*`, `upload.*`, `files.*`, `viewer.*`, `media.*`, `pdf.*`, `diff.*`, `burn.*` in both `src/messages/en.json` and `ar.json`; add a CI script failing on key mismatch (FE-021). Add locale-aware `formatBytes`, `formatDate`, `formatRelative` using the active locale (`Intl` with `ar-EG`/`en`) replacing hard-coded `en-US` (A10). Viewer SSR: pick locale from `Accept-Language` for first paint, then localStorage (no cookies on zaa.gl). API error messages already bilingual fallbacks (`lib/api-client.ts:13-17`).
- **RTL**: code, diff, editor, line numbers, filenames, slugs and URLs are always `dir="ltr"` with `unicode-bidi: isolate`; prose (`text`/`markdown`) `dir="auto"`; diff columns stay old-left/new-right inside the LTR code region while labels translate; directional icons use the existing `rtl:rotate-180` pattern (`LinkForm.tsx:234`); progress bars, toolbars and drawers mirror via MUI direction. Loupe/pan maths use physical pointer coordinates (unaffected by RTL).
- **Accessibility**: editor has visible label + `aria-describedby` hint, no Tab trap by default; Copy buttons announce via toast + `aria-live`; diff lines carry `+/−` text and `<span class="sr-only">added</span>`; image viewer controls are labelled buttons with keyboard shortcuts listed in a help popover, loupe `aria-hidden`; `alt` = owner-provided alt text or filename; video uses native controls; upload progress `role="progressbar"` + polite live region; burn confirm dialog focuses Cancel first; all states reachable without pointer; colour contrast via MUI palette; reduced motion respected (`globals.css:101-108`, GIF poster).
- **Responsive**: `/paste/new` editor full width, options panel stacks under on `< md` (same pattern as `LinkForm.tsx:486`); diff unified on `< md`; library grid 2 cols xs → 4 cols lg, table view on ≥ md; image viewer full-bleed with bottom toolbar on mobile, pinch zoom native; download page single column, sticky download button on mobile.

---

## 7. Testing strategy

- **Unit/component**: `vitest`, `@vitejs/plugin-react`, `jsdom`, `@testing-library/react`, `@testing-library/user-event`, `@testing-library/jest-dom`, `msw` (API mocks shared with E2E). Targets: language registry/detection order, `formatBytes`, filename bidi sanitiser, upload queue reducer (pause/resume/retry/token refresh), gate state machine, Markdown renderer XSS corpus (no `<script>`, no `javascript:`, remote images → links), PasswordGate, ConfirmDialog tone. Async Server Components are not unit-testable in Vitest → covered by E2E.
- **E2E**: `@playwright/test` + `@axe-core/playwright`, against `next build && next start` with a mock API (MSW node server or a fixture server on `NEXT_PUBLIC_API_URL` and the internal API URL) and a real tus server fixture (`@tus/server` + `@tus/file-store`) to test interruption/resume. Projects: Chromium + WebKit + mobile viewport; matrix en/ar × dark/light screenshots for viewers. Later a smoke suite against the real backend (handoff §4 step 3).
- **Security E2E**: SVG with `<script>`/`onload`, Markdown with HTML/`javascript:` links, filename containing U+202E, oversized snippet, burn page fetched by a bot (`GET` twice) still downloadable once; assert CSP header present and no inline-script violations (listen to `securitypolicyviolation`).
- **Contract**: generate types from the backend OpenAPI with `openapi-typescript` (spec `:796`) and fail CI on drift.
- **Budgets**: per-route first-load JS checked in CI (FE-023).
- Scripts: `test`, `test:e2e`, `typecheck` (`tsc --noEmit`), plus existing `lint`.

---

## 8. Tasks

Format: **ID · title · priority** — Description. Files. Deps. AC (acceptance). Tests.

**FE-001 · Phase 1 foundation and Next 16 verification · P0** — Install deps, read `node_modules/next/dist/docs/` on proxy.ts, route handlers/request streaming, body limits, `'use cache'`, CSP nonces, `assetPrefix`; record findings in this doc. Create `(viewer)` group (move `p/f/i`), `ViewerLayout`, `loading/error/not-found` per viewer; turn UPCOMING items (`app-shell.tsx:58-69`) into NAV entries (`/paste/new`, `/files`) with active matching; add `NEXT_PUBLIC_CONTENT_URL`, `NEXT_PUBLIC_UPLOAD_URL`, server-only `ZAAGL_INTERNAL_API_URL` to `lib/config.ts` and `.env.example`.
  - *Files:* `app/(viewer)/**`, `components/app-shell.tsx`, `lib/config.ts`.
  - *Deps:* ARC-xxx(hosts).
  - *AC:* build passes; nav keyboard-reachable; viewers render a skeleton under Suspense with no cookie set.
  - *Tests:* Playwright nav + `Set-Cookie` absent on `/p/x`.

**FE-002 · Test tooling · P0** — Add Vitest+RTL+MSW, Playwright+axe, scripts, CI job, MSW handler skeletons for §5.
  - *Files:* `vitest.config.ts`, `playwright.config.ts`, `src/test/**`, `e2e/**`, `package.json`.
  - *Deps:* —.
  - *AC:* `npm test` and `npm run test:e2e` run green on CI with one sample each for an existing Phase 0 screen.
  - *Tests:* self.

**FE-003 · Security headers and CSP · P0** — `proxy.ts` (or `headers()`), nonce-based CSP for app and viewer routes, Emotion cache `nonce` (`lib/mui-bridge.tsx:30`), `Referrer-Policy: no-referrer` on viewers (signed URLs), `X-Content-Type-Options`, `frame-ancestors 'none'`, `Permissions-Policy`. Policy: `script-src 'self' 'nonce-…' 'strict-dynamic'; img-src 'self' data: blob: <content>; media-src <content>; connect-src 'self' <api> <upload> <content>; worker-src 'self' blob:; object-src 'none'; base-uri 'none'`.
  - *Deps:* OPS-xxx(nginx), ARC-xxx(content domain).
  - *AC:* no CSP violations on all pages in E2E; nonce present on Next and Emotion tags.
  - *Tests:* E2E violation listener; header snapshot.

**FE-004 · Shared libs refactor · P0** — Move `copyText`, `download`, `queryString` to `lib/browser.ts`/`lib/url.ts`; locale-aware `formatBytes/formatDate/formatRelative`; `sanitizeFilename` (strip bidi/control chars, split extension); `lib/server-api.ts` (`server-only`, internal URL, no token).
  - *Files:* `lib/*`, `features/links/{api.ts,QrPreview.tsx}`.
  - *Deps:* —.
  - *AC:* Phase 0 behaviour unchanged.
  - *Tests:* unit for formatters/sanitiser.

**FE-005 · Extract LinkForm sections · P0** — `AccessPolicyFields`, `OrganizeFields`, `ShareResultCard` (copy + QrPreview) from `LinkForm.tsx:80-180, 340-488`; LinkForm consumes them.
  - *Deps:* —.
  - *AC:* identical LinkForm UX; components accept a `capabilities` prop (maxViews, burn).
  - *Tests:* component tests for each.

**FE-006 · Types and query hooks · P0** — `lib/types.ts` (Snippet, Version, Resource, Upload, PublicContent, plan limits), `features/snippets/api.ts`, `features/files/api.ts`, query keys `["snippets"…]`, `["resources"…]`; later swap to OpenAPI-generated types.
  - *Deps:* BE-xxx(API-S*, U*, R*, V*, B1).
  - *AC:* hooks typed, MSW mocks for every endpoint.
  - *Tests:* unit with MSW.

**FE-007 · Highlighting module · P0** — `lib/languages.ts` registry (≥ 100 langs, Shiki↔hljs↔ext), `lib/highlight/server.ts` (singleton highlighter, `codeToHast` → JSX, dual themes), `lib/highlight/client.ts` (lazy core + JS engine), CSS for themes/line numbers.
  - *Deps:* FE-001.
  - *AC:* Server page renders highlighted code with 0 KB added client JS; theme switch without re-highlight; > 512 KB falls back to plain.
  - *Tests:* unit (registry, fallback), E2E bundle check.

**FE-008 · SnippetEditor + LanguagePicker + detection · P0** — §3.1–3.2.
  - *Files:* `features/snippets/{SnippetEditor,LanguagePicker}.tsx`, `useLanguageDetection.ts`.
  - *Deps:* FE-007.
  - *AC:* editor only in `/paste/new` chunk; detection order respected; Tab not trapped; RTL UI keeps editor LTR; textarea fallback works.
  - *Tests:* unit detection order; E2E typing, paste, a11y scan.

**FE-009 · /paste/new page · P0** — Form (kind, title, language, filename, AccessPolicyFields incl. burn-after-read, OrganizeFields), plan limits, submit → ShareResultCard with QR; "Create another".
  - *Files:* `app/(app)/paste/new/page.tsx`, `features/snippets/NewSnippetView.tsx`.
  - *Deps:* FE-005, FE-006, FE-008, BE-xxx(API-S1).
  - *AC:* create succeeds, link copied if chosen, errors mapped (413/409/422), en/ar strings.
  - *Tests:* E2E happy path + errors.

**FE-010 · Snippet viewer /p/[slug] · P0** — §3.3 with server fetch, line anchors, wrap, Copy Raw, Raw, Download, expiry footer, gate/reveal integration.
  - *Files:* `app/(viewer)/p/[slug]/page.tsx`, `features/snippets/SnippetViewer.tsx`.
  - *Deps:* FE-007, FE-012, BE-xxx(API-V1, V4).
  - *AC:* public snippet works with JS disabled (read-only); no `dangerouslySetInnerHTML`; GET never consumes.
  - *Tests:* E2E incl. no-JS, anchors, copy.

**FE-011 · Markdown rendering · P1** — §3.4.
  - *Files:* `features/snippets/MarkdownView.tsx`.
  - *Deps:* FE-007, ARC-xxx(markdown).
  - *AC:* XSS corpus inert; remote images as links; code fences highlighted; `dir="auto"`.
  - *Tests:* unit corpus; E2E render.

**FE-012 · Viewer gates and states · P0** — Refactor `UnlockForm` → `PasswordGate` (`onUnlocked`), `RevealInterstitial`, state views (scanning/expired/burned/blocked), same-origin route handlers `app/api/view/[slug]/{unlock,reveal,download}/route.ts` (no cookies, grant kept in memory).
  - *Deps:* FE-001, FE-004, BE-xxx(API-V2, V3, V5).
  - *AC:* Phase 0 `/unlock` unchanged; wrong password/429/410 handled; reload re-gates.
  - *Tests:* unit state machine; E2E each state.

**FE-013 · Uploader (tus) · P0** — §3.9.
  - *Files:* `features/files/{Uploader.tsx,useUploadQueue.ts,uploadClient.ts}`.
  - *Deps:* FE-005, FE-006, BE-xxx(API-U1..U4), OPS-xxx(files host CORS).
  - *AC:* 1 GB upload survives network drop and page reload; pause/resume/cancel; account token never sent to upload host; plan-limit errors bilingual; progress announced.
  - *Tests:* unit reducer; E2E with tus fixture + offline toggle.

**FE-014 · Files & media library /files · P0** — Grid/list, kind tabs, search, status chips, quota bar, drop zone, row menu (copy link, QR, open, download via API-R4, delete with ConfirmDialog); filters in URL.
  - *Files:* `app/(app)/files/page.tsx`, `features/files/FilesView.tsx`.
  - *Deps:* FE-013, BE-xxx(API-R1, R2, R4, B1).
  - *AC:* empty/loading/error states; thumbnails from signed URLs refresh before expiry.
  - *Tests:* E2E list/filter/delete; axe.

**FE-015 · Image viewer /i/[slug] (+ SVG, GIF) · P0** — §3.6–3.7.
  - *Files:* `features/media/{ImageViewer,Loupe,BackgroundSwitch}.tsx`, `app/(viewer)/i/[slug]/page.tsx`.
  - *Deps:* FE-012, BE-xxx(API-V1), OPS-xxx(content domain).
  - *AC:* pixel-identical to original (hash of downloaded bytes = upload); backgrounds; loupe 2–8×; keyboard zoom; SVG only via `<img>` from content domain.
  - *Tests:* E2E (bytes hash, SVG script inert, keyboard), visual en/ar.

**FE-016 · Video player for recordings · P1** — §3.8 in `/i/[slug]`.
  - *Deps:* FE-015, BE-xxx(transcode variants).
  - *AC:* plays on Chromium/WebKit/mobile; poster; seek works (Range).
  - *Tests:* E2E playback start.

**FE-017 · Download page /f/[slug] + burn · P0** — §3.10.
  - *Files:* `features/files/FileViewer.tsx`, `app/(viewer)/f/[slug]/page.tsx`.
  - *Deps:* FE-012, BE-xxx(API-V1, V5).
  - *AC:* burn confirm before consume; bot GETs don't consume; second download shows burned state; filename spoof neutralised.
  - *Tests:* E2E burn flow, double-GET, spoofed name.

**FE-018 · PdfPreview · P1** — §3.11 lazy, hardened.
  - *Deps:* FE-017, ARC-xxx(pdf preview), OPS-xxx(content CORS/Range).
  - *AC:* `/f` non-PDF pages load no pdf.js; first page < 2 s on 10 MB PDF (local); eval disabled.
  - *Tests:* E2E chunk absence + render.

**FE-019 · Versions and diff · P1** — §3.5, "new version" from owner page.
  - *Files:* `features/snippets/{VersionList,DiffView}.tsx`.
  - *Deps:* FE-010, BE-xxx(API-S3..S5, V4).
  - *AC:* side-by-side ≥ md, unified below; not colour-only; URL `?v=`/`?diff=`.
  - *Tests:* unit hunk renderer; E2E.

**FE-020 · Owner detail pages /paste/[id], /files/[id] · P1** — Preview, settings (AccessPolicyFields), analytics (reuse `LinkAnalyticsView` panels), delete.
  - *Deps:* FE-009, FE-014, BE-xxx(API-S2, R2).
  - *AC:* edits persist; delete returns to library.
  - *Tests:* E2E.

**FE-021 · i18n/RTL completeness · P1** — All new keys en+ar, key-parity script, `Accept-Language` first paint on viewers, RTL review of every new screen.
  - *Deps:* all UI tasks.
  - *AC:* parity script green; no hard-coded strings (lint rule or grep check).
  - *Tests:* script in CI; screenshot matrix.

**FE-022 · Accessibility pass · P1** — axe on every new route, keyboard walkthrough, SR labels.
  - *Deps:* UI tasks.
  - *AC:* 0 serious/critical axe issues.
  - *Tests:* Playwright+axe.

**FE-023 · Bundle budgets · P2** — Analyzer (*verify* Turbopack support), CI budgets: viewer `/p` ≤ +25 KB gz over Phase 0 baseline, `/f` non-PDF ≤ +30 KB, `/paste/new` ≤ 260 KB lazy.
  - *Deps:* FE-009/010/017.
  - *AC:* CI fails on regression.
  - *Tests:* CI.

**FE-024 · Viewer metadata/OG · P2** — `generateMetadata` with generic text for gated/burn content, `robots: noindex`, title = owner title for public items only, OG image = worker thumbnail for public images.
  - *Deps:* BE-xxx(API-V1).
  - *AC:* unfurl of gated item reveals nothing.
  - *Tests:* E2E head assertions.

**FE-025 · Library integration in /links · P1** — Type column/icon + kind filter in `LinksView.tsx:185-255`, row links go to the right owner page.
  - *Deps:* BE-xxx(API-R3).
  - *AC:* mixed list works; filters combine.
  - *Tests:* E2E.

**FE-026 · Security regression suite · P1** — XSS/SVG/Markdown/filename/burn fixtures from §7 as a dedicated Playwright project.
  - *Deps:* FE-003, FE-010, FE-015, FE-017.
  - *AC:* all inert.
  - *Tests:* self.

**FE-027 · BFF readiness spike · P1** — Isolate token access behind an `authTransport` interface in `lib/api-client.ts:73-99` so a BFF cookie session (spec §7.13) can replace localStorage without touching features; write migration note.
  - *Deps:* ARC-xxx(BFF).
  - *AC:* no feature imports `tokenStore` directly except auth.
  - *Tests:* unit.

**FE-028 · Paste-to-share shortcut in dashboard · P3** — Global `Ctrl/Cmd+Shift+V` in dashboard: clipboard text → `/paste/new` prefilled, image → upload.
  - *Deps:* FE-009, FE-013.
  - *AC:* works in Chromium; permission errors handled.
  - *Tests:* E2E (Chromium).


---

## 9. Open questions

1. **ARC-xxx(hosts)**: are viewers on `zaa.gl/p|i|f/*` (Nginx path-routing to Next, `assetPrefix` on app host) or on a separate `view.` host? Spec §1.2 says every other path on `zaa.gl` is 404 today. Spec examples use `/s/<id>` for snippets; tech stack and repo use `/p/` — pick one before the reserved list is final.
2. **ARC-xxx(content domain)**: which registrable domain serves raw bytes, and is it the same as the DNS-only upload host (tech stack §09)?
3. **ARC-xxx(BFF)**: will the BFF/HttpOnly cookie (spec §7.13) land before Phase 1 GA? If not, accept the localStorage + CORS risk explicitly in `DECISIONS.md`.
4. **ARC-xxx(tiers)**: Free/Starter/Pro (PRD) vs Guest/Free/Premium (spec). Can guests share snippets/files? Exact size/retention/burn per tier, and is "permanent storage" on Pro compatible with "every link expires" (spec `:34`)?
5. **Burn semantics**: consume on grant issue or on completed transfer (interrupted download problem)? Does "X days" apply together with burn? Do owners' own downloads count?
6. **ARC-xxx(markdown)**: frontend `react-markdown` (recommended) or backend Markdig+HtmlSanitizer HTML?
7. **ARC-xxx(pdf preview)**: can the worker render PDF page images (removes pdf.js from the client), or do we ship react-pdf?
8. **ARC-xxx(resources)**: one `/api/resources` model with `kind`, or separate `/api/snippets` + `/api/files` + `Link.kind`? Do analytics count viewer page views, raw fetches, or downloads?
9. Snippet limits (max bytes/lines) and whether private ("unlisted vs password") snippets exist beyond link gates.
10. Video: will FFmpeg transcode every recording (H.264 MP4) or only non-browser-playable formats? Max duration per tier?
11. Should SVG uploads be sanitised/rasterised server-side, or is `<img>` + sandboxed content domain enough?
12. Client-side encryption (WebCrypto, tech stack §01) — Phase 1 or later? It changes uploader, viewer and burn design.
13. Does the API `302` from `zaa.gl/<code>` to `/p|i|f/<code>` count the click (Phase 0 analytics), so the viewer must not double-count?
