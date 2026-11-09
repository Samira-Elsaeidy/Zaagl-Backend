# Stage 1 review, round 3: frontend (FE-004, FE-005, FE-006)

Reviewer: zaagl-head. Date: 2026-11-09. Checkout `C:\Users\pc\Zaagl-Frontend`, branch `phase1/stage-1`, HEAD `985948d` (unchanged, nothing committed). I read the diffs and the new files. I re-ran one cheap check myself (node, below). I did not run tsc, lint, tests or the build; the agent reports they pass (61 tests).

## Verdicts

| Task | Verdict | Owner |
|---|---|---|
| FE-004 shared libs (`lib/browser.ts`, `url.ts`, `format.ts`, `filename.ts`) | REWORK (one-line fix in `filename.ts` plus a test) | frontend |
| FE-005 LinkForm split (`AccessPolicyFields`, `OrganizeFields`, `ShareResultCard`) | ACCEPT | – |
| FE-006 draft content types and hooks (`lib/content-types.ts`, `features/snippets/api.ts`, `features/files/api.ts`, `api-client` headers) | ACCEPT WITH NOTES | frontend |
| Dev-server 500 on `[slug]` routes | NOT_TESTED / open: must be explained before Stage 2 | frontend |

## FE-004

- **Bug (verified): `sanitizeFilename` does not replace backslashes.** `RESERVED_CHARS = /[<>:"/\|?*]/g`: inside a JS character class, `\|` is an escaped pipe, so `\` is not in the set. The doc comment ("plus both path separators") is wrong. My check, which loads the regex from `src/lib/filename.ts` with node: `"a\\b/c|d..\\..\\evil"` → `"a\\b_c_d..\\..\\evil"`, so the backslashes survive. Windows treats `\` as a path separator, so `..\..\evil.exe` keeps its traversal shape in a download name.
  - **Fix:** `const RESERVED_CHARS = /[<>:"/\\|?*]/g;`
  - Add a test: `sanitizeFilename("..\\..\\evil.exe")` must contain no `\`.
  - The existing test only covers `/` (`filename.test.ts:28`).
- The rest of `filename.ts` is good: NFC normalisation, control and bidi characters stripped (ZWNJ/ZWJ kept), Windows device names, byte-based truncation that keeps the extension, and a fallback name.
- `format.ts`:
  - Number and date output depends on the locale. Arabic uses `ar-u-nu-arab` (Arabic-Indic digits and Arabic month names). Formatters are cached, and slugs and URLs are never passed through these helpers.
  - **This is a visible change on existing Phase 0 screens.** Before, everything was formatted as `en-US`; Arabic users now see ٢٥ instead of 25 on dashboards and charts. That is a product choice, question U-9.
  - `formatBytes` uses decimal units (1 MB = 1,000,000 bytes), but ARC-005's configuration uses binary values (25 MB = 26,214,400 bytes), so a Free limit would display as "26 MB". This must be settled with the user (question U-3). Then frontend and backend use the same definition.
- `browser.ts` and `url.ts`: straight moves of `copyText`, `downloadBlob` and `queryString` out of `links/api.ts` and `QrPreview.tsx`. `queryString` now also drops `null`, which has no effect on existing callers.

## FE-005: no behaviour change

- Same fields, same order, labels, hints, `maxLength`, gating (`gated` → "upgrade" for password, click limit and routing; the fallback URL is always shown; the scan notice is always on) and initial values (`accessDraftFromLink` is the old initialisers, moved). `buildInput` only destructures the two draft objects, and its validation is unchanged.
- Folder creation moved into `OrganizeFields` with the same behaviour. `CollapsibleCard` uses MUI `Collapse`, so collapsing does not reset the form state. The new-folder text lives in `OrganizeFields`, as it did in `LinkForm` before.
- `ShareResultCard` renders the same `Card`, `QrPreview` and children.
- New capability values (`"hidden"`, `showPublicStats`) are not used by the links form today. They prepare the content forms.

## FE-006

- Drafts only: no screen imports them. The headers name the source contracts and mark unknowns OPEN.
- Secrets: `linkKey` is documented as shown once and never cached or logged. Viewer descriptors are fetched server-side (no browser hook). Filenames are marked untrusted.
- `api()` takes extra `headers`. The BFF forwards only its allow-list (`server/bff/handlers.ts:94-107`: `idempotency-key`, `captcha-token`, `if-match`, `range`, …; never `Cookie`/`Authorization`), so a caller cannot smuggle credentials through.
- **No token in the browser (checked):**
  - No `accessToken`, `refreshToken` or `access_token` outside `src/server/**` and tests.
  - `localStorage` is used only for the locale, the theme and `removeLegacyTokens` (which deletes old tokens).
  - `useLiveStats` test asserts there is no `access_token` in the hub URL.
- Notes for when these drafts become real (Stage 2):
  - `X-Zaagl-Link-Key` / `X-Zaagl-Grant` are not in the BFF allow-list. That is fine while viewers fetch server-side; add them deliberately if a client route ever needs them.
  - `ContentProblemCode` lacks `content-link`, `already-finalized`, `checksum-mismatch` and `range-required`. Align it with the architect's corrected catalogue (architect review, list A item 9).
  - `useOwnerDownload` sends `{ disposition }`, which is not in the contracts. Check it against the OpenAPI document when it is fixed.
  - `useResourceStatus` polls every 2 s and ignores `Retry-After`, although the comment says to honour it. Fix this when the hook is first used.

## Dev server `[slug]` 500

- The agent reports: on `next dev`, `[slug]` routes answered 500 "Jest worker encountered 2 child process exceptions"; the production build serves them with 200. The cause is unknown and the dev server has since been stopped.
- That is not a pass. Record it as NOT_TESTED / open.
- Before Stage 2 (QA and E2E use `next dev`), the frontend agent must:
  1. Reproduce the 500.
  2. Capture the child-process error. It is in the terminal, not the HTTP body.
  3. Report the cause: config, a server-only import in a client module, or the environment.

## Process

- Stayed in `Zaagl-Frontend/src/**`. No commits, branches or worktrees. Claims tsc, lint, build and 61 tests pass; I did not re-run them.
