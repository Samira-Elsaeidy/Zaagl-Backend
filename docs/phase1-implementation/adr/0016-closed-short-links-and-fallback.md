# ADR-0016: Closed short links are answered by zaa.gl; fallback only for expired links

Status: Accepted 2026-11-09 (PD-39 and PD-42, product owner). Addendum §3.2, §17. Contract C3. Spec v9.4 §4.3, §7.3.

## Context
Phase 0 redirects expired, disabled and deleted links to the app (`{Frontend:BaseUrl}/expired`) or to the owner's
fallback URL, treating all three alike (`ResolutionEndpoints.cs:48-49`, `AccessGate.cs:36`). Spec §7.3 requires small
static pages from `zaa.gl` with the right status, and a disabled (abuse) link must never send visitors anywhere the owner
chose.

## Decision
- `purgeable` → `404` page. `deleted` → `410` page. `disabled` → `410` "disabled" page (abuse/safety: acceptable-use
  page linking to the report form). All served by `zaa.gl`, spec §7.3 headers, no cookie, `HEAD` same status.
- `quarantined` (expired): with an owner-set fallback URL → `302` to it (`Cache-Control: private, max-age=0`); without
  one → `410` "expired" page.
- Disabled and deleted links always get the `410` page, never the fallback. Content links have no fallback.
- The fallback gets the same checks as a destination (PD-44, ADR-0020).
- A fallback `302` counts toward the miss limits like the `410` it replaces.

## Consequences
- Nothing is redirected to `app.zaa.gl` for a closed link; the app's `/expired` page is no longer reached from `zaa.gl`.
- OPEN: whether a fallback `302` counts as a click or records analytics (until decided: nothing recorded).
- Click-limited links: settled by PD-43 and PD-47 (ADR-0019).

## Rejected options
- Keep the app redirect: contradicts spec §7.3 and leaks closed codes into the app.
- Fallback for disabled links: lets an abusive owner keep redirecting after a takedown.
