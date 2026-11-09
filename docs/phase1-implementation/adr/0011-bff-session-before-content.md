# ADR-0011: BFF session before any Phase 1 content renders in the app

Status: Accepted 2026-11-09 (PD-23). Addendum §4.3. Spec §7.13.

## Context
The web app keeps tokens in `localStorage` today. Phase 1 renders untrusted Markdown, SVG, PDFs and filenames on
`app.zaa.gl`; one XSS would expose the account.

## Decision
FE-029 moves to the spec §7.13 BFF session in Stage 1: tokens in an encrypted `HttpOnly` host-only `__Host-` cookie,
CSRF double-submit on every state change, no tokens in browser script. It is a gate before Stage 2 content ships.

## Consequences
The viewer calls the API through the BFF (link-key and grant headers forwarded). CSRF tokens also satisfy the
same-site mitigation of ADR-0003.

## Rejected options
- Ship with `localStorage` tokens and accept the risk.
