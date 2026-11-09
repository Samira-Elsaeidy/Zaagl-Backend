# Stage 1 review, round 3: architect (contract freeze, ARC design notes, OpenAPI, ADR-0023)

Reviewer: zaagl-head. Date: 2026-11-09. Checkout `C:\Users\pc\Zaagl-Backend`, branch `phase1/stage-1`, HEAD `9923526` (unchanged, nothing committed). I read the stub files and the diffs myself. A read-only sub-review cross-checked contracts.md, the seven ARC notes, openapi-phase1.yaml and ADR-0023 against the Approvals table, and I re-checked a sample of its findings in the files (marked "verified"). No build was run (a backend build is in progress). The stubs compile only if the backend build passes, which the lead must confirm.

## Verdicts

| Item | Verdict | Owner of follow-up |
|---|---|---|
| Contract stubs (Links `ContentLinkContracts.cs`, `LinkContracts.cs` additions; Access `AccessContracts.cs`; Analytics `ClickEventKind`; Billing `ContentPlanContracts.cs`, `BillingStatusDto.Capabilities`; Identity `NativeClientContracts.cs`; Files/Media/Resources/Snippets contracts) | ACCEPT WITH NOTES | architect |
| contracts.md + ARC-002/004/005/006/008/012/014 | ACCEPT WITH NOTES (consistency pass needed, list A) | architect |
| openapi-phase1.yaml | ACCEPT WITH NOTES (list A) | architect |
| ADR-0023 app CSP (PROPOSED) | REWORK before it is put to the user (`connect-src` breaks viewers) | architect |

## Stubs: no logic, approved decisions respected

- Records, enums, constants and interfaces only. The only member with a body is `ContentOwner.IsGuest => UserId is null` (trivial). There are no implementations, no DI registrations and no behaviour change. New parameters on existing records (`LinkDto`, `LinkSnapshot`, `ClickEvent`, `BillingStatusDto`) all have defaults, so Phase 0 callers and cached JSON are unaffected.
- Matches the approvals: link key on by default (`RequireLinkKey = true`, `Public = false`; PD-01/37/45), viewer paths p/i/f (PD-02), tus 90,000,000-byte chunks (PD-12), no Pro (PD-04), guest identity and guest bytes deleted at expiry (PD-06/14), burn rules in `IDownloadGrants` (PD-05), and no Admin on device tokens (PD-07).
- Items still to confirm are labelled in the stubs (guest file cap, storage totals, burn/password per plan, PD-09/13/16/17/18/30).
- Notes:
  1. `DeviceFlowWire.VerificationUri` hard-codes `https://app.zaa.gl/activate`. Staging needs a different host, so it must come from configuration (the constant may stay as the production default).
  2. `IStoredObjects.QuarantineAsync` (takedown keeps the bytes under a legal hold) and `IContentHashBlocklist.AddAsync` ("also takes down every live resource with that hash") describe evidence retention and mass takedown. That area is PD-11 (OPEN, counsel). Add "PD-11" to both doc comments so they are not read as decided.
  3. `ClickEventKind.View/Raw/Download` are part of the PD-10 proposal. The comment already says the headline metric is not defined. That is acceptable.

## Product questions the documents treat as settled (fix the wording, or ask the user)

- **PD-28 (PROPOSED)**: a share is usable while its file is still being scanned. C8 `CompleteUploadResponse` returns the link at completion (contracts.md:353), and ARC-006 T1/T2 binds the link before the scan. The OpenAPI document labels this; contracts.md and ARC-006 do not. Question to the user, U-5 below.
- **PD-15 / PD-25 (PROPOSED)**: local disk and database task tables. C7 `GetInternalPath`/X-Accel and contracts.md:423 "drained … `FOR UPDATE SKIP LOCKED`" assume both. PD-51 approved BE-004 and BE-013, which are built exactly this way. Question to the user, U-1 below.
- **PD-17 misquoted (verified)**: contracts.md:271 says "PD-17 proposed (256 KB / 512 KB / 2 MB)", but PD-17 has no guest value; the 256 KB guest cap is new. ARC-005 sets `MaxSnippetBytes = 0` (fail-closed) while the unconfirmed guest file cap of 25 MiB is live (ARC-005:26, verified). Make both fail-closed until the user answers, or neither. Question U-2.
- **Custom alias on content**: `customSlug` appears in C1/C8/C9 and the OpenAPI document, but no rule says who may set one (plan gate? guests?). Question U-4.
- **ARC-006 Q2 already answered in its own table**: T16 releases storage at takedown (ARC-006:111) while Q2 asks the user. Mark T16 "pending Q2".
- **Unlabelled user-visible numbers**: device-approval lockout of 10 failures in 10 min → 15 min (ARC-014:72); non-burn tickets valid 30 min / 16 requests (ARC-006:182-185); raw snippet download name `<code>.txt` (ARC-008:171). Label each as a technical default.
- **PD-33 divergence**: PD-33 proposes headless Linux CLI credentials by environment variable only, while ARC-014 Q2 recommends a protected file. The user should see both options (question U-7).
- **PD-38 "risk to confirm in ARC-008"**: ARC-008 never mentions the Cloudflare terms or PD-52. Add one paragraph saying that PD-52 options (b) and (d) would change the host design (C3).

## List A: consistency fixes (architect, before BE-007 and the Stage 2 backend work start)

1. `/d/` whole object above the cap: openapi:690 says "answered as a 206 part", but ARC-008:116/130 say `416 range-required` (verified). Pick one. 416 is the safer choice (no surprise partial bodies).
2. The 410 code on `/d/`: a single `gone` (ARC-008:113) vs `expired/deleted/disabled/taken-down` (openapi `GoneOrBurned`, ARC-004).
3. Maximum length of tickets and grants: 256 (ARC-008:63, ARC-004:108) vs 512 (openapi:682, :920).
4. Burn-claim columns `claimed_grant_id`/`requests_used` (ARC-008) vs `burn_grant_id`/`request_count` (ARC-006). There is also a rule conflict: ARC-008:147 requires "link not expired" inside the claim, while ARC-006:175 lets a link that expires in between still serve its one download. This touches PD-05, so state the rule once.
5. Scopes: `files:write` (ARC-014:170) vs `resources:write` (openapi:503/526, not one of C11's six). `TokenScopes` in the stub has no `resources:write`.
6. Device flow: field limits 64/32 (ARC-014:53) vs 100/50 (openapi). Rate-limit policies `device-*` (ARC-014) vs `auth` (openapi).
7. Unlocking a link with no password: `400 validation` (ARC-004:75) is missing from the OpenAPI responses.
8. Download counts come from two sources: the grant table (C6, openapi:1193) and `ClickEventKind.Download` events (ARC-008:93). Keep one source of truth (PD-10 is still proposed).
9. The contracts.md error catalogue (:384-387) is missing `content-link`, `range-required`, `version-conflict`, `diff-too-large`, `checksum-mismatch` (460), `insufficient-storage` (507), `busy` (503) and `already-finalized`. The backend's `ErrorCodes` (BE-002) already has `content-link`, `checksum-mismatch` and `already-finalized`; align the two lists.
10. Minor items:
    - Reconciliation source (ARC-012:132 vs ARC-006:154).
    - `keep_until` vs `legal_hold_until`.
    - The C4 code block in contracts.md lacks `LinkState`/`PasswordStamp`.
    - The C9 table lacks `POST /api/uploads/simple`.
    - contracts.md:9 lists approvals only up to PD-47; update it to PD-52.

## ADR-0023: REWORK before the user is asked

- **Class N `connect-src 'self'`** (ADR-0023:85, :100, verified) blocks the viewers' `fetch` to `usercontent.zaa.gl`, which ARC-008 needs for Range-part assembly and react-pdf. Add `https://usercontent.zaa.gl` (from configuration) to `connect-src` on viewer pages.
- DevOps WAF rule C3 and ARC-008 must then agree on `OPTIONS` (CORS preflight) on `usercontent.zaa.gl`. ARC-008 allows GET/HEAD/OPTIONS, while ops-001 C3 blocks everything except GET/HEAD.
- **Class S rationale** (ADR-0023:76, :102-104): the owner's content library (`(app)/files`, `paste`) will render untrusted filenames and snippet text, including content created by API keys. Either move these pages to class N or justify the choice again.
- After the rework, the ADR goes to the user as question U-6.

## Process

- Stayed in its own files (docs, `*/Contracts/**` stubs). No commits, branches or worktrees. Nothing was tested, and nothing claims to be.

## Consolidated questions for the user (all round-3 reviews; U-numbers are used in the other three files)

**Needed before Stage 2**
- U-1. Storage and background jobs (PD-15, PD-25). The approved groundwork (PD-51) already stores files on the server's own disk and runs background jobs from database queues (not Wolverine or Cloudflare R2). Confirm these two choices? Options: confirm; or revisit. Recommendation: confirm. R2 can be added later behind the same interface.
- U-2. Snippet size limits (PD-17). Options:
  - (a) guest 256 KB, Free 512 KB, Starter 2 MB;
  - (b) guest = Free 512 KB, Starter 2 MB.

  Recommendation: (a). Snippets cannot be created at all until this is answered.
- U-3. What does "MB" mean in plan limits? Options:
  - (a) 1 MB = 1,000,000 bytes, matching what the app displays;
  - (b) 1 MB = 1,048,576 bytes.

  Recommendation: (a), so "25 MB" means the same thing everywhere.
- U-4. Who may choose a custom alias (`zaa.gl/my-name`) for a snippet or file? Options:
  - (a) the same accounts as for links, never guests;
  - (b) nobody in Phase 1.

  Recommendation: (a).
- U-5. Can a file's link be shared while the virus scan is still running (PD-28)? Recommendation: yes. The link works at once, shows "processing", and serves no bytes until the scan is clean. The desktop app needs this.
- U-6. Approve the page security policy for the web app (ADR-0023, option c) once the architect fixes it so viewers can load files. Recommendation: approve after the fix.
- U-7. CLI sign-in on Linux servers without a desktop (PD-33). Options:
  - (a) environment variable only;
  - (b) a file only the user can read.

  How long a CLI or desktop sign-in lasts is also still open; the architect proposes a value in ARC-014. Recommendation: (b) plus the environment variable for CI; lifetime as ARC-014 proposes.

**Later (Stage 3 or after)**
- U-8. Storage totals, guest file cap and plan features:
  - Per-account storage totals: Free 250 MB, Starter 5 GB?
  - Guest file cap: 25 MB?
  - Password on content: Starter only, like links?
  - Burn after downloading: for every signed-in account?

  Recommendation: yes to each. The following also come later, with recommendations in ARC-006 and PD-10:
  - Delete bytes immediately on owner delete.
  - Free storage at takedown.
  - What counts as a "view".
- U-9. Arabic screens: show Arabic-Indic digits (٢٥) or Western digits (25)? The app now shows ٢٥. Recommendation: keep ٢٥ unless the target market prefers Western digits.
- U-10. Staging addresses: pay about US$10 a month for a Cloudflare certificate add-on, or rename staging hosts to one level (`staging-api.zaa.gl`)? Recommendation: rename (free; small spec edit).
- U-11. Should `www.zaa.gl` forward to `app.zaa.gl`? Recommendation: yes.
- U-12. Procurement and accounts:
  - email provider;
  - `security@zaa.gl` mailbox;
  - spare domain;
  - admin IP list;
  - Cloudflare, registrar and Google Search Console access.
- U-13. Remove the four old agent worktrees under `.claude/worktrees` (from an earlier commit)? Recommendation: yes, after the lead checks they hold no unsaved work.
