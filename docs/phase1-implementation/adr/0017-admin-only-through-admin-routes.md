# ADR-0017: Admins manage other users' links only through /api/admin/*

Status: Accepted 2026-11-09 (PD-40, product owner). Addendum §18. Contract C9. Amends spec v9.4 §7.4.

## Context
Spec §7.4 lists the owner routes (`GET/PATCH/DELETE /links/{code}`, renew, stats, archive) as "owner or Admin". The
admin routes already require the Admin role, TOTP and `Admin:AllowedNetworks`; letting an admin token act through the
owner routes bypasses the network restriction and mixes two authorization models.

## Decision
- Owner routes are owner-only. Any other principal, an admin included, gets `404`, indistinguishable from an unknown
  code. An admin is the owner of their own links like any user.
- Admins act on other users' links and content only through `/api/admin/*`, with the actions those routes offer.
- The same applies to the Phase 1 owner routes (`/api/resources*`, `/api/snippets/{id}*`, `/api/me/storage`).
- Spec §7.4's authorization table ("or Admin") and renewal step 2 ("owner or admin") are amended accordingly.

## Consequences
- QA's generated authorization tests expect `404` for "admin JWT with TOTP" on another user's resource through an owner
  route; the SpecGap §7.4 cases change.
- Any admin action not offered under `/api/admin/*` (for example renewing a user's link) needs a product decision.

## Rejected options
- Keep "owner or Admin" on owner routes: bypasses the admin network and TOTP gates.
