# ARC-010 · Snippet model and rendering safety

| | |
|---|---|
| Owner | zaagl-architect. Implementers: backend (Snippets module, Api endpoints, delivery hop branch), frontend (editor `/paste/new`, viewer `/p/[slug]`, owner pages), CLI (`zaagl paste`, `zaagl get`). |
| Status | DESIGN for Stage 2. Contracts: `src/Modules/Snippets/Contracts/SnippetContracts.cs` (stub) plus the post-freeze changes F-4 to F-6 in contracts.md. |
| Rules | Addendum §2.2-2.4, §4.3-4.4, §5, §9, §10, §11, §12, §16. PD-01, PD-06, PD-14 (as updated), PD-17 (as changed by PD-56), PD-23, PD-36, PD-41, PD-53, PD-54, PD-55, PD-56, PD-57 (APPROVED). |
| OPEN, not decided here | **PD-09 Markdown renderer location** (needed now, Stage 2; §6.3 gives both rule sets and the recommendation). Burn after view for snippets (PD-05 note). PD-10 view counting. ARC-002 Q1 retry and key. |
| Related | ARC-002, ARC-003, ARC-004, ARC-006, ARC-008, ARC-014, ADR-0023, `openapi-phase1.yaml` (Snippets tag). |

## 1. Rules in one place

| Rule | Value | Source |
|---|---|---|
| Size cap per version, UTF-8 bytes of the stored text | guest 262,144; Free 524,288; Starter 2,097,152 (`ContentLimits.MaxSnippetBytes`) | PD-56, PD-54 units |
| Versions per snippet | at most 100 (`Snippets:MaxVersions`) | PD-17 |
| Diff | between two versions of **one** snippet only | PD-17 |
| Guests | may create (CAPTCHA, 30/h and 60/24 h per IP partition with uploads, 7 days, no quarantine); may **not** edit, delete, list or see versions | PD-14, addendum §11.9 |
| Aliases | `customSlug` allowed for everyone, guests included; Phase 0 alias rules | PD-57 |
| Default visibility | keyed (`#k=`); `public: true` is an explicit owner choice; guests: public **to confirm**, refused until then (§4.1 step 3) | PD-01, addendum §5.1.3 |
| Password | Starter only (`PasswordOnContent`), else `402 plan_required` | PD-54 |
| Expiry | `min(requested, now + RetentionDays)`; later → `400 expiry-beyond-plan`; editing never changes expiry | addendum §9.1 |
| Storage | every stored version counts toward the owner's `ContentQuota` (Free 250 MB, Starter 5 GB); guests have no ledger | PD-54, ARC-006 §1, §4 |
| Monthly link cap | content does not count toward Free's 50 URL links **only if PD-08 is approved**; until then the issuer path skips the cap (ARC-002 §6 row 3) | PD-08 PROPOSED |

## 2. Tables (schema `snippets`, owned by Snippets)

```sql
CREATE SCHEMA IF NOT EXISTS snippets;

CREATE TABLE snippets.snippets (
  resource_id     uuid PRIMARY KEY,               -- = resources.resources.id (no cross-schema FK, module rule)
  current_version int  NOT NULL CHECK (current_version BETWEEN 1 AND 1000),
  version_count   int  NOT NULL CHECK (version_count BETWEEN 1 AND 1000),
  total_bytes     bigint NOT NULL CHECK (total_bytes >= 0),   -- Σ size_bytes of all versions (quota cross-check)
  created_at      timestamptz NOT NULL,
  updated_at      timestamptz NOT NULL,
  CHECK (current_version = version_count)          -- versions are never removed individually (§3)
);

CREATE TABLE snippets.snippet_versions (
  resource_id     uuid NOT NULL REFERENCES snippets.snippets(resource_id) ON DELETE CASCADE,
  version         int  NOT NULL CHECK (version >= 1),
  content         text NOT NULL,                  -- exactly the UTF-8 text received (§4.3); no NUL (PostgreSQL text)
  language        text NULL CHECK (language ~ '^[a-z0-9][a-z0-9+#._-]{0,63}$'),
  render_mode     smallint NOT NULL CHECK (render_mode IN (0,1,2,3)),   -- SnippetRenderMode
  size_bytes      int  NOT NULL CHECK (size_bytes BETWEEN 1 AND 2097152),
  line_count      int  NOT NULL CHECK (line_count >= 1),
  sha256          bytea NOT NULL CHECK (octet_length(sha256) = 32),     -- of the UTF-8 bytes; evidence (C12), no-op detection
  reservation_id  uuid NULL,                      -- quota reservation for versions >= 2 (§5)
  quota_committed boolean NOT NULL DEFAULT true,  -- false between insert and CommitToResource (§5)
  created_via     smallint NOT NULL,              -- Resources.Contracts.CreatedVia
  created_at      timestamptz NOT NULL,
  PRIMARY KEY (resource_id, version)
);
CREATE INDEX ON snippets.snippet_versions (created_at) WHERE quota_committed = false;   -- repair job (§5)

-- Immutability: no UPDATE of a version row, ever. DELETE only through the kind handler (purge, orphan sweep).
CREATE FUNCTION snippets.forbid_version_update() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.resource_id = OLD.resource_id AND NEW.version = OLD.version AND NEW.content = OLD.content
     AND NEW.language IS NOT DISTINCT FROM OLD.language AND NEW.render_mode = OLD.render_mode
     AND NEW.size_bytes = OLD.size_bytes AND NEW.sha256 = OLD.sha256 THEN
    RETURN NEW;                                   -- only quota_committed may change (false -> true)
  END IF;
  RAISE EXCEPTION 'snippet versions are immutable' USING ERRCODE = 'P0001';
END $$;
CREATE TRIGGER trg_snippet_versions_immutable BEFORE UPDATE ON snippets.snippet_versions
  FOR EACH ROW EXECUTE FUNCTION snippets.forbid_version_update();
```

Notes
- Title, owner, expiry, visibility, password and state live in Resources / Links, never here (single source).
  `ISnippetReader` fills `SnippetViewDto.Title` and `ExpiresAt` from `IResourceRegistry.GetAsync` (Snippets references
  Resources.Contracts, BE-006).
- `content` is PostgreSQL `text` (TOAST-compressed on disk). Quota counts the **logical UTF-8 size**, not the compressed
  size, so the numbers the user sees are predictable. Worst case per snippet: 100 × 2 MB = 200 MB (Starter), bounded by
  the 5 GB quota; Free 100 × 512 KB = 50 MB, bounded by 250 MB.
- `line_count` = number of `\n` plus 1 when the text does not end with `\n` (a text ending in `\n` has exactly as many
  lines as newlines). `\r\n` counts once. Computed by the backend; clients display it, never send it.
- The `CHECK` upper bound on `size_bytes` is the largest plan cap; the per-plan cap is enforced in the application.
- Migrations: new schema, expand-only, own DbContext (`SnippetsDbContext`, history table in schema `snippets`).

## 3. Versions

- Version 1 is written at create. Every successful `PUT /api/snippets/{id}` appends version `current + 1`; nothing is
  ever overwritten. `language` and `render_mode` are per version (an edit may change them); `title` is resource-level
  (`PATCH /api/resources/{id}`).
- Optimistic concurrency: the request carries `baseVersion`; the append is
  `UPDATE snippets.snippets SET current_version = current_version + 1, version_count = version_count + 1,
  total_bytes = total_bytes + @size, updated_at = @now WHERE resource_id = @id AND current_version = @base
  AND version_count < @max RETURNING current_version`, followed by the version `INSERT`, in one transaction with
  `SET LOCAL lock_timeout = '3s'`. Zero rows: re-read; `current_version != base` → `409 version-conflict`;
  `version_count >= max` → `409 version-limit`.
- No-op edit: if `sha256`, `language` and `render_mode` equal the current version's, no version is written and the
  current `SnippetVersionDto` is returned with `200` (no quota change).
- Limit reached (100): `409 version-limit`. The snippet stays readable and shareable; the owner can create a new
  snippet. Nothing is dropped automatically (Q2).
- Single versions cannot be deleted; deleting the snippet removes all of them at purge (PD-55). Q3 asks whether an
  owner should be able to remove one version (for example a pasted secret).
- Idempotency on `PUT`: optional `Idempotency-Key`, scope `u:<owner>`, request hash over
  `id \n baseVersion \n sha256 \n language \n renderMode`; a replay returns the stored `200`, so a client retry after a
  lost response does not turn into `409 version-conflict`.

## 4. Create and edit flows

### 4.1 Create (`POST /api/snippets`, JSON; and `POST /api/snippets/raw`, §4.4)

1. Credentials (PD-36, addendum §19): no credential → guest path; any failing credential → `401` before anything else.
   API key needs `snippets:write` (`403 insufficient-scope`).
2. Guest only: Turnstile `Captcha-Token`, action `guest-snippet` (BE-026 fixes the name) → `401 captcha-required|invalid`.
3. Validate (no I/O): content present and non-empty; strict UTF-8 encoding (`new UTF8Encoding(false, true)`; a lone
   surrogate from JSON → `400 validation`); no U+0000 (`400 validation`, PostgreSQL `text` cannot hold it);
   `language` null or matches the column regex after `ToLowerInvariant()`; `renderMode` one of four (default `code`);
   `title` ≤ 200 characters after stripping control and bidi-override characters (U+202A-202E, U+2066-2069);
   `customSlug` by the Phase 0 alias rules; **guest with `public: true` → `400 validation`** until addendum §5.1.3 is
   confirmed (fail-closed; guests are always keyed).
4. Idempotency claim (spec §7.2 step 4; `Idempotency-Key` required on JSON create): scope `u:<id>` or the guest creator
   key; request hash over the canonical JSON fields plus `sha256(content)`.
5. Plan (`IContentPlanProvider`, C5): `size > MaxSnippetBytes` → `413 snippet-too-large`; password without
   `PasswordOnContent` → `402 plan_required`; expiry clamp (`400 expiry-beyond-plan`).
6. Guest counters (30/h, 60/24 h per IP partition, shared with uploads; `429 quota`), inside the creation transaction
   (addendum §11.3). Enforced only where BE-030 is deployed (addendum §11.6).
7. `IResourceRegistry.RegisterAsync(Kind=Snippet, owner, DeclaredSizeBytes = size, ExpiresAt, Title,
   BurnAfterDownload=false, CreatedVia)` → `Pending`, quota reserved (`403 quota-exceeded`).
8. Snippets inserts `snippets` (version 1) and `snippet_versions` (v1, `quota_committed = true`: version 1 is covered by
   the registration's reservation).
9. `ILinkIssuer.CreateContentLinkAsync(Kind=Snippet, ResourceId, owner, customSlug, ExpiresAt, password,
   RequireLinkKey = !public)` (idempotency key = resource id; alias errors `409` as Phase 0).
10. `IResourceRegistry.BindLinkAsync`, then `MarkReadyAsync(resourceId, size)` (ARC-006 T3: commit, `bytes_state = 1`).
11. `201 SnippetCreatedDto` (`linkKey` once). Complete the idempotency record.

Crash safety: after 7 or 8 without a link, the orphan sweeper (ARC-006 T19) deletes the resource and calls
`IResourceKindHandler.OnResourceDeletedAsync` (Snippets deletes its rows, releasing the reservation through Resources).
After 9 without 10, a retry with the same `Idempotency-Key` replays the same resource id and the issuer's idempotency
returns the same link (key behaviour: ARC-002 Q1). A link whose resource is still `Pending` shows `processing` (ARC-003
§4.1 step 6), never text.

`CreatedVia` is derived by the Api, never sent by clients: device token `client_id = zaagl-cli` → `Cli`,
`zaagl-desktop` → `Desktop`; API key → `Api`; web session or guest → `Web`.

### 4.2 Edit (`PUT /api/snippets/{id}`)

Owner only (any other principal, admins included, `404`, PD-40). Requires capability `ManageContent` (Free and
Starter true, guests have no route). Steps: validate as 4.1 step 3 (no title, no alias); resource state must be
`Ready`, else `410 <state code>` (`expired`, `deleted`, `disabled`, `taken-down`) or `409 processing`; size against the
owner's **current** plan cap (`413`); version limit and concurrency (§3); quota (§5); `200 SnippetVersionDto` of the new
version. Editing does not change the link, the key, the expiry or the short URL; the shared link shows the new version
at once.

### 4.3 Text fidelity

The stored text is exactly what was received: no trimming, no line-ending conversion, no BOM removal, no Unicode
normalisation. Raw delivery returns the same bytes. The only transformations are the CLI's explicit `ansi=strip`
(§4.4) and the rejection rules above.

### 4.4 CLI text ingest (`POST /api/snippets/raw`)

- Auth: J (web or device session) or K(`snippets:write`). **No guest path** (ARC-014 §4 matrix); no credential → `401`.
- Body: `Content-Type: text/plain` with `charset=utf-8` or no charset (UTF-8 assumed); any other charset or type →
  `415 unsupported-media-type`. A `Content-Length` above the plan cap → `413` before reading; otherwise the body is
  read through a counting stream that stops at cap + 1 bytes → `413 snippet-too-large` (the request is never buffered
  beyond the cap). Invalid UTF-8 or U+0000 → `415 unsupported-media-type` (binary input; the CLI should detect this
  first and suggest `zaagl share <file>` instead).
- Query: `lang`, `title`, `renderMode` (default `code`), `ansi=keep|strip` (default `strip`; applies only to
  `renderMode=terminal`, otherwise ignored), `expiresAt`, `public` (default false), `customSlug`. No password parameter
  (secrets must not travel in a query string); a Starter user who wants a password uses the JSON route or
  `PATCH /api/resources/{id}` afterwards.
- `ansi=strip` removes ECMA-48 sequences: CSI (`ESC [ ... final byte 0x40-0x7E`), OSC (`ESC ]` ... `BEL` or `ESC \`),
  other two-byte `ESC x`, and C1 controls in the 0x80-0x9F range; tabs, `\r` and `\n` stay. The size cap applies to the
  **received** body; the stored size is the size after stripping.
- `Idempotency-Key` optional. Response: `Accept: text/plain` → `201` with body = share URL incl. `#k=<key>` and `\n`
  (exactly one line, so `| pbcopy` works); otherwise `201 SnippetCreatedDto` JSON.
- Rate limit `content-write` per principal (values ARC-013, OPEN); a device or web principal is never subject to the
  guest counters.

## 5. Quota accounting (ContentQuota, Resources)

| Event | Quota effect | Mechanism |
|---|---|---|
| Create (v1) | reserve `size` at register, commit `size` at ready | existing `RegisterAsync` / `MarkReadyAsync` (ARC-006 T1, T3) |
| Edit (v n ≥ 2) | `+size` of the new version | **F-4**: `ReserveForResourceAsync(resourceId, size)` → Snippets transaction (§3, stores `reservation_id`, `quota_committed = false`) → `CommitToResourceAsync(reservationId, size)` → set `quota_committed = true` |
| Edit refused after reserve (`version-conflict`, `version-limit`, lock timeout) | none | `ReleaseAsync(reservationId)` |
| No-op edit | none | no reservation is taken (sha256 compared before reserve) |
| Owner delete | none until purge | PD-55 (ARC-006 T15) |
| Purge, guest expiry | release `total` | `ReleaseUsedAsync(resourceId)` releases the resource's whole `quota_usage.bytes` (ARC-006 T18, T13) |
| Takedown | per ARC-006 Q2 (OPEN) | rows kept while `bytes_state = 2` (§9) |

Repair (crash between steps): a Snippets task every 10 minutes commits versions with `quota_committed = false` older
than 1 minute (`CommitToResourceAsync` is idempotent per reservation). Resources' sweeper releases reservations in
state `reserved` that carry a `resource_id` and are older than 24 hours. Both directions are fail-safe: an
uncommitted reservation still counts as reserved, so the quota is never exceeded while repair is pending.

`quota_usage` (ARC-006 §1) keeps one row per resource; `CommitToResourceAsync` adds to that row's `bytes` and to the
ledger's `used_bytes` under the owner's advisory lock. Daily reconciliation (ARC-012) additionally compares, per
resource, `quota_usage.bytes` with `snippets.total_bytes` through `ISnippetReader.GetTotalBytesAsync` (F-6) and alerts
on drift.

## 6. Rendering safety (viewer and owner pages on `app.zaa.gl`)

### 6.1 Ground rules (all modes)

1. The API returns snippet text as a JSON string only. No endpoint returns HTML derived from user text, unless PD-09
   chooses option B (§6.3).
2. The app never inserts user text as markup: no `dangerouslySetInnerHTML`, `innerHTML`, `insertAdjacentHTML`,
   `document.write` or `new Function` with user data. A lint rule (`react/no-danger` as error, plus a grep test for
   `innerHTML`) enforces it in `src/features/snippets/**` and `src/app/p/**`.
3. CSP: the per-request nonce policy of ADR-0023 on `/p/*` (PD-53); `script-src 'nonce-…' 'strict-dynamic'`,
   `object-src 'none'`, `base-uri 'none'`, `frame-ancestors 'none'`, `img-src 'self' data:` plus the usercontent host
   only for media viewers. Trusted Types (`require-trusted-types-for 'script'` on `/p/*`, no policy created, so any
   string-to-HTML sink throws) SHOULD be added if the ADR-0023 spike shows Next.js runs under it; if not, rule 2's
   lint and grep checks stand alone.
4. The viewer has no sign-in token in page script (PD-23, PD-41); the BFF session cookie is `HttpOnly`.
5. Very large texts: the viewer virtualises lines (a 2 MB single-line minified file must not freeze the tab); syntax
   highlighting is skipped above `Snippets:MaxHighlightBytes` (FE constant, proposed 1 MB) with "plain view" shown.

### 6.2 Modes

| `renderMode` | Viewer behaviour |
|---|---|
| `code` | Shiki highlighting through `codeToHast` + `hast-util-to-jsx-runtime` (React elements, no HTML string). Language: §8. Line numbers, wrap toggle. |
| `plain` | Text in a `<pre>`, no highlighting, no link detection. |
| `markdown` | §6.3. A "source" toggle shows the raw text as `plain`. |
| `terminal` | Text with ANSI **SGR colour/style only** (`ESC[…m`, 16/256/truecolour, bold, italic, underline), parsed by a small allowlist parser into `<span>` elements with classes or inline `style` objects built from numbers (never from user strings). Every other escape is dropped, explicitly including OSC 8 hyperlinks, OSC 52 clipboard writes, cursor movement and title changes. |

Bidi and invisible characters: in `code`, `plain` and `terminal`, bidi controls (U+202A-202E, U+2066-2069) and
zero-width characters are shown as visible markers (Trojan Source, CVE-2021-42574), with a banner "this text contains
hidden direction characters". The page's own RTL layout (Arabic UI) does not change the text's direction: code blocks
are always `dir="ltr"`, Markdown prose uses `dir="auto"` per block.

### 6.3 Markdown (PD-09 OPEN, decision needed for Stage 2)

Both options produce the same **allowed output**:

| Allowed elements | `p`, `h1`-`h6`, `em`, `strong`, `del`, `code`, `pre`, `blockquote`, `ul`, `ol`, `li`, `a`, `hr`, `br`, `table`, `thead`, `tbody`, `tr`, `th`, `td`, `input[type=checkbox][disabled]` (GFM task lists), `sup` + `section` for footnotes |
|---|---|
| Allowed attributes | `a[href]` (schemes `http`, `https`, `mailto` only, after entity decoding and whitespace/control removal; relative and `#` links refused), `th/td[align]`, `ol[start]`, `code[class=language-*]`, `id` only with the prefix `user-content-` |
| Links | `rel="nofollow ugc noopener noreferrer"`, `target="_blank"`; the visible text is kept; a link whose text is a URL different from its `href` shows the real host on hover |
| Images | **never** `<img>` from Markdown: `![alt](url)` renders as a link "image: alt" (blocks tracking pixels and IP leaks; the CSP would block them anyway) |
| Raw HTML | never rendered: inline and block HTML are shown as text (escaped), not dropped silently |
| Headings and footnotes | ids prefixed `user-content-` (no DOM clobbering of `window.name`, `location`, form ids) |
| Code blocks | highlighted as §6.2 `code` with the fence's language |
| Limits | nesting depth ≤ 32 (deeper input rendered as text), render time budget: above `Snippets:MaxMarkdownRenderBytes` (proposed 1 MB) show source view with a "render" button |

- **Option A (recommended in PD-09): client rendering.** `react-markdown` + `remark-gfm`, `skipHtml: false` with HTML
  nodes mapped to escaped text, no `rehype-raw`, custom `urlTransform` implementing the scheme rule, `components`
  overrides for `a` and `img`, `allowedElements` = the table above, `unwrapDisallowed: true`. Output is React elements;
  there is no HTML string anywhere, so there is no sanitizer to bypass. Matches the tech stack's Shiki/hljs placement.
- **Option B (tech stack text): server rendering.** Markdig with `DisableHtml()` and the GFM extensions only
  (pipe tables, task lists, strikethrough, autolinks, footnotes), then HtmlSanitizer (Ganss.Xss) configured with the
  same allowlist, returned as an additional `renderedHtml` field. The viewer would have to inject it as HTML through a
  named Trusted Types policy; one sanitizer bug (mXSS) becomes an XSS on `app.zaa.gl`. Cached per version (immutable).

Until PD-09 is decided the API returns text only (both options need that) and the FE builds option A behind the
`markdown` mode; if B is chosen, the backend adds `renderedHtml` (nullable, additive) and the FE swaps the component.

### 6.4 XSS test corpus (acceptance for ARC-010; QA runs it later, PD-51)

Each vector is stored as a snippet in every render mode, opened in the viewer and owner pages (en and ar), and must
produce no script execution, no network request to a non-allowlisted origin and no CSP violation report:
1. OWASP XSS Filter Evasion Cheat Sheet, all vectors, as `plain`, `code` (several languages) and `markdown`.
2. Markdown links: `[x](javascript:alert(1))`, mixed case, `&#106;avascript:`, `java&#x09;script:`, `java\tscript:`,
   `\x00javascript:`, `data:text/html,…`, `vbscript:`, `file:`, reference-style `[x][1]` / `[1]: javascript:…`,
   autolinks `<javascript:alert(1)>`, link titles breaking out `[x](https://a "\" onmouseover=\"…")`.
3. Images: `![x](javascript:…)`, `![x](https://attacker/pixel)` (must not load), SVG data URLs.
4. Raw HTML: `<script>`, `<img src=x onerror>`, `<svg onload>`, `<iframe>`, `<object>`, `<embed>`, `<math>`,
   `<template>`, `<style>`, `<form action>`, `<meta http-equiv=refresh>`, `<base href>`, `<a href=javascript:>`.
5. mXSS: `<noscript><p title="</noscript><img src=x onerror=…>">`, namespace confusion (`<svg><style>`,
   `<math><mtext><table><mglyph><style>`), comment/CDATA breakouts.
6. DOM clobbering: headings and footnotes named `__proto__`, `constructor`, `location`, `name`, `cookie`.
7. Resource exhaustion: 10,000 nested `>` or `[`, 100,000 `*`, a 2 MB single line, 100,000 short lines.
8. Terminal mode: OSC 8 links, OSC 52 clipboard, title change, cursor moves, malformed CSI, 24-bit colour with
   out-of-range values.
9. Unicode: bidi overrides in code and titles, zero-width joiners, homoglyph URLs in Markdown links, Arabic text mixed
   with code.
10. Metadata sinks: `title`, `language` and `customSlug` with HTML and quotes, rendered in `<title>`, Open Graph meta
    (ARC-003 §5), the owner list and the share dialog.

## 7. Raw endpoint (H4 on `usercontent.zaa.gl`)

- Minted by the descriptor (`GET /api/public/content/{code}`, gate purpose `Raw` in addition to `View`) and by the
  owner routes (`GET /api/snippets/{id}` returns `rawUrl` minted with purpose `Owner`): `IDownloadGrants.MintAsync(
  resourceId, Raw|Owner, derivative = "v<n>")`. The ticket format is unchanged (C3; derivative field, ≤ 32 ASCII).
- Delivery hop (`/internal/delivery/{ticket}`, ARC-008 §4.3): steps 1-2 unchanged; then for `ResourceKind.Snippet` the
  hop does **not** send `X-Accel-Redirect`. It reads the version through `ISnippetReader.GetRawAsync(resourceId, n)` and
  answers itself; Nginx passes an upstream body through unchanged and adds the `ucd-headers.conf` set (ARC-008 §10).
- Response headers from the hop: `Content-Type: text/plain; charset=utf-8` (whatever the language or mode, Markdown
  and HTML snippets included), `Content-Disposition: attachment; filename="<code>.txt"; filename*=UTF-8''<code>.txt`
  (addendum §4.4 makes raw an attachment; see Q1), `Content-Length`, `ETag: "<hex sha256>"`, `Accept-Ranges: bytes`
  (`Results.Bytes(..., enableRangeProcessing: true)`; single range only). No `Set-Cookie`, no `Cache-Control` (Nginx
  sets `private, no-store`), no CORS header (purpose Raw is not `Inline`/`Preview`).
- Added by Nginx on every response, errors included: `X-Content-Type-Options: nosniff`, `Content-Security-Policy:
  sandbox; default-src 'none'; …`, `Cache-Control: private, no-store`, `Referrer-Policy: no-referrer`,
  `Cross-Origin-Resource-Policy: cross-origin`, `X-Robots-Tag: noindex, nofollow`.
- HEAD: same headers, no body, never counted. Size is always below `Content:MaxResponseBytes` (2 MB max), so the
  `416 range-required` rule never triggers.
- State: only `Ready` serves (addendum §2.4 invariant); otherwise the ARC-008 §4.5 mapping (`410 gone`, `403
  ticket-invalid`). No analytics event (`raw`, kind 2) until PD-10.

## 8. Language

Order (tech stack §14): the language the user picked → auto-detection → plain text.

- Canonical ids are **Shiki language ids**, lowercase (`typescript`, `csharp`, `python`, `bash`, `json`, `markdown`, …).
  The frontend owns the supported list (`src/lib/snippet-languages.ts`: id, display name, file extensions, hljs name),
  generated from the Shiki bundle; the CLI embeds a copy of the extension map.
- The backend stores the id the client sent (syntax check only, column regex) and never detects. An id the viewer does
  not know renders as plain text, so an old client or a typo never breaks a snippet.
- Auto-detection runs in the browser with `highlight.js` `highlightAuto` restricted to the supported subset, only when
  `language` is null and `renderMode = code`; detection result below relevance 5 → plain text. The viewer labels it
  "auto-detected" and never writes it back. The editor may show a detected suggestion; it is stored only if the user
  accepts it (then it is the user's pick).
- CLI: `--lang` wins; else the extension of `--filename`/the file argument through the embedded map; else null (viewer
  detects). Piped stdin without `--lang` sends null.

## 9. Kind handler, takedown, abuse

- `IResourceKindHandler.OnResourceDeletedAsync` (Snippets): `DELETE FROM snippets.snippets WHERE resource_id = @id`
  (cascade to versions); idempotent.
- Resources MUST NOT call it while `bytes_state = 2` (legal hold): taken-down snippet text stays in the database as
  evidence until the hold ends (ARC-006 T16, T21). The text is unreachable meanwhile because the link is disabled.
- Evidence (C12): `sha256` and `resource_id` of the current version; `object_key` is null for snippets.
- The content hash blocklist (addendum §13.4) applies to files; checking snippet hashes is not required in Phase 1.
- Web Risk on URLs inside snippets: not required (addendum §13.5).

## 10. Diff

- `GET /api/snippets/{id}/diff?from=&to=` (owner only, `resources:read`). `from != to` (else `400 validation`), both
  versions exist (else `404`), either order allowed.
- DiffPlex `SideBySideDiffBuilder` (line diff, `ignoreWhiteSpace` from query `ignoreWhitespace`, default false), mapped
  to `SnippetDiffDto` (`ChangeType` names lowercased: `unchanged`, `deleted`, `inserted`, `imaginary`, `modified`).
- Limits: combined line count ≤ `Snippets:MaxDiffLines` (20,000) and the computation runs under a 2-second
  `CancellationToken`; either exceeded → `413 diff-too-large`. Combined bytes are bounded by 2 × the plan cap.
- Response `Cache-Control: private, max-age=3600` with `ETag: "d-<from>-<to>-<ws>"` (versions are immutable); the BFF
  passes it through.
- Visitors: no diff route in Phase 1 until ARC-003 Q2 is answered.

## 11. Contract changes (post-freeze, contracts.md F-4 to F-6)

Source-compatible additions; the backend applies them in the stubs with BE-006/BE-0xx (Snippets).

```csharp
// F-4  Resources.Contracts.IContentQuota
/// Reserve bytes for a new snippet version of an existing resource. Owner taken from the resource; guests → validation.
Task<Result<QuotaReservation>> ReserveForResourceAsync(Guid resourceId, long bytes, CancellationToken cancellationToken);
/// Commit a reservation into the resource's existing usage row (adds to quota_usage.bytes). Idempotent per reservation.
Task<Result<bool>> CommitToResourceAsync(Guid reservationId, long actualBytes, CancellationToken cancellationToken);

// F-5  Snippets.Contracts.SnippetVersionDto (trailing, optional)
public sealed record SnippetVersionDto(int Version, int SizeBytes, int LineCount, DateTime CreatedAt,
    string? Language = null, SnippetRenderMode RenderMode = SnippetRenderMode.Code);

// F-6  Snippets.Contracts.ISnippetReader (new members)
Task<IReadOnlyDictionary<Guid, SnippetSummaryDto>> GetSummariesAsync(IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken);
Task<long?> GetTotalBytesAsync(Guid resourceId, CancellationToken cancellationToken);
public sealed record SnippetSummaryDto(int Version, string? Language, SnippetRenderMode RenderMode, int LineCount);
```

`ISnippetReader.GetRawAsync` returns the text of version `n` (null = current); the hop encodes it as UTF-8. The Api
composes owner and public DTOs from `ResourceDescriptor` + Snippets DTOs (Resources never references Snippets).

## 12. Review checklist

- [ ] Versions immutable (trigger), `current_version = version_count`, concurrent edits: exactly one wins, the other
      gets `409 version-conflict`; the 101st version gets `409 version-limit`.
- [ ] Size cap on UTF-8 bytes, per plan, guest 256 KB; raw ingest stops reading at cap + 1.
- [ ] Every stored version is counted in the quota exactly once; no-op edits count nothing; refused edits release.
- [ ] Guests: create only; `public: true` refused; no `PUT`/`DELETE`/versions/diff.
- [ ] No HTML from user text anywhere in the API (unless PD-09 = B); viewer has no `dangerouslySetInnerHTML`.
- [ ] Raw: `text/plain; charset=utf-8`, attachment, nosniff, sandbox CSP, no cookie, only `Ready`.
- [ ] Text round-trips byte-for-byte (CRLF, BOM, trailing spaces, Arabic, emoji) through create, raw and descriptor.
- [ ] Kind handler never deletes under legal hold.

## 13. Questions for the user

- Q0 (PD-09, OPEN, needed now) Where is Markdown rendered: in the browser with raw HTML switched off (recommended), or
  on the server with Markdig plus a sanitizer (tech stack text)?
- Q1 (ARC-008 Q3) Should "View raw" open as text in the browser tab (safe: `text/plain` under the sandbox CSP) instead
  of downloading a `.txt` file? Needs an addendum §4.4 change; until then raw downloads.
- Q2 When a snippet reaches 100 versions: refuse further edits (built behaviour) or drop the oldest version
  automatically?
- Q3 May an owner delete one single old version (for example after pasting a secret by mistake)? Today versions can only
  go away with the whole snippet.
- Q4 Do older versions count toward the storage quota (built: yes, every stored version counts)?
- Q5 (addendum §5.1.3, to confirm) May guests create public (keyless) snippets? Until answered they are always keyed.
- Q6 (PD-05 note) Burn after view for snippets: build it in Phase 1, or not?
