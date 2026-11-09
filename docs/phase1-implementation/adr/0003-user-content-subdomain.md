# ADR-0003: User bytes are served from usercontent.zaa.gl

Status: Accepted 2026-11-09 (PD-03, changed by the product owner from "separate registrable domain"). Addendum §4.
Contract C3.

## Context
User files (HTML, SVG, PDF) must never execute in an origin that holds credentials. The draft recommended buying a
separate registrable domain. The product owner chose a `zaa.gl` subdomain instead. ARC-001 names it
`usercontent.zaa.gl`.

## Decision
- All bytes are served only from `https://usercontent.zaa.gl/d/<ticket>`, via Nginx `X-Accel-Redirect`.
- Mandatory mitigations (condition of the approval):
  1. No cookie is ever set on `usercontent.zaa.gl`, and no host sets a cookie with `Domain=zaa.gl` (including edge or
     CDN cookies; OPS verifies).
  2. App cookies stay host-only `__Host-`.
  3. Every response carries `nosniff` and `CSP: sandbox; default-src 'none'; ...`; anything outside the inline
     allowlist is `Content-Disposition: attachment`.
  4. CSRF tokens on every state-changing app (BFF) route.
  5. The host accepts no credentials; access is by ticket only.

## Consequences
- Accepted risk: the subdomain is same-site with `app.zaa.gl`, so `SameSite` cookies do not separate them (hence CSRF
  tokens), and abuse on hosted files can hurt the reputation of `zaa.gl` itself (Safe Browsing listings apply to the
  zone). Abuse response targets and zone monitoring are launch requirements.
- No domain purchase; one zone, one certificate set.
- Resolved by PD-38 (ADR-0015): the host is proxied by Cloudflare, with every response bounded per request.

## Rejected options
- Separate registrable domain (`<ucd>`): stronger isolation, rejected by the product owner.
- Serving bytes from `api.zaa.gl` or `app.zaa.gl`: credential-bearing origins.
