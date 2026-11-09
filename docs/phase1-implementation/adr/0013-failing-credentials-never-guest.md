# ADR-0013: A failing credential is answered 401, never treated as a guest

Status: Accepted 2026-11-09 (PD-36, product owner). Addendum §11.1, §19. Contract C9.

## Context
`POST /api/links` (and in Phase 1 the snippet and upload create routes) accept guests. If a request with a wrong,
revoked or expired credential silently falls back to guest creation, the user's link is created without an owner,
under guest limits and without statistics, and a revoked key keeps creating links. Spec §7.2 step 1 and MUST NOT #27
forbid this.

## Decision
- A request is a guest only when it carries no credential at all.
- Any credential that fails (wrong, revoked, expired, malformed, bad signature, superseded security stamp, API key whose
  owner lost the `ApiKeys` capability, enrol-only token) gets `401` with `WWW-Authenticate`, before CAPTCHA
  verification, guest counters or any reservation.
- A valid credential without the scope gets `403 insufficient-scope`, also never guest.
- The BFF never turns a browser whose session cannot be refreshed into a credential-less call.
- Fixed for `POST /api/links` in Stage 1 (backend); binding for every Phase 1 guest endpoint: `POST /api/snippets`,
  `POST /api/uploads` and `POST /api/uploads/simple` reject a failed credential with `401` the same way (Head note).

## Consequences
- Clients see a revoked key immediately instead of creating orphan guest links.
- Tests: every failing-credential variant × every guest-capable route returns `401`, and no guest counter moves.

## Rejected options
- Fall back to guest on authentication failure: hides revocation and loses ownership.
