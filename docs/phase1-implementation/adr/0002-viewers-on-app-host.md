# ADR-0002: Viewers live on app.zaa.gl/p|i|f, zaa.gl stays a redirector

Status: Accepted 2026-11-09 (PD-02). Addendum §3, §4.1. Contracts C1, C3.

## Context
Spec §1.2 and §4.5 rule 6: `zaa.gl` never sets cookies and content must never run in its origin. The frontend already
has `app/p/[slug]`, `i/[slug]`, `f/[slug]` pages.

## Decision
- Every share is a Link (`target_kind` + `resource_id`). `zaa.gl/<code>` runs the state checks and answers
  `302 https://app.zaa.gl/{p|i|f}/<code>` for content links.
- Path names `p` (snippet), `i` (media), `f` (file). `zaa.gl/s|i|f|p/...` stay reserved and return 404 in Phase 1.

## Consequences
- One namespace: aliases, leased codes, takedown, analytics and reports work for content unchanged.
- `zaa.gl` never renders content; the viewer is trusted UI that shows user content only as inert data.
- One extra hop per view (302).
- The viewer API re-runs every gate because it can be reached without the redirect.

## Rejected options
- Viewers on `zaa.gl/p|i|f/*` routed to Next.js: content UI in the cookie-free redirect origin, breaks spec rules.
- A separate `view.` host: no benefit over `app.zaa.gl` once the BFF session exists (ADR-0011).
- Path `s/` from the spec example: the frontend already uses `p/`.
