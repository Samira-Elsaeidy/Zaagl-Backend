# ADR-0019: Click-limited links: 410 page on GET, no destination on HEAD

Status: Accepted 2026-11-09 (PD-43 and PD-47, product owner). Addendum §17.5, §17.6. Contract C3. Extends ADR-0016.

## Context
Phase 0 sends a URL link whose `MaxClicks` budget is used up to its fallback URL or the app's "expired" page
(`ResolutionService.cs:109`), and answers `HEAD` on a click-limited link with the destination without checking the budget
(`ResolutionService.cs:77-80`), so link checkers and chat previews learn a destination the owner limited.

## Decision
- **PD-43:** `GET` on a link whose click budget is used up → small static page "This link has reached its click limit"
  from `zaa.gl`, status `410`, spec §7.3 headers, **never the fallback URL**. Counts toward the miss limits.
- **PD-47:** `HEAD` on any link with a click limit, budget left or not → `410` without a body and without `Location`;
  it never reveals the destination and never consumes a click. Links without a click limit are unaffected: `HEAD`
  answers like `GET` without counting.

## Consequences
- Link checkers report click-limited links as unavailable; that is intended.
- Content links have no click limit in Phase 1 (PD-10 not approved), so neither rule applies to them yet.

## Rejected options
- Fallback for exhausted links: the owner limited access; the fallback is for expiry only (PD-42).
- `HEAD` reporting the destination: leaks it without spending a click.
