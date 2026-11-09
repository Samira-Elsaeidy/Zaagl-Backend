# ADR-0015: usercontent.zaa.gl is proxied by Cloudflare with per-request bounded delivery

Status: Accepted 2026-11-09 (PD-38, product owner). Addendum §4.1, §4.1.1, §8.1.7, §20. Contract C3. Completes
ADR-0003.

## Context
ADR-0003 left open whether `usercontent.zaa.gl` is proxied by Cloudflare or DNS-only. Proxying hides the origin and
applies the WAF; Cloudflare Free limits the size of each HTTP request and response, not of a whole file.

## Decision
- `usercontent.zaa.gl` goes through Cloudflare (proxied).
- Every response body is at most `Content:MaxResponseBytes` (to confirm; proposed 90,000,000 bytes, the same bound as
  upload requests). Larger objects are delivered in parts: `Range` requests (`206`) or chunked download parts, each
  under the limit.
- Uploads stay chunked tus on `api.zaa.gl` (PD-12, ADR-0008).
- Cloudflare never caches the host's responses (`Cache-Control: private, no-store` plus a cache-bypass rule).
- Pre-launch check: confirm Cloudflare's self-serve terms on serving large non-web files through the CDN.

## Consequences
- In Phase 1 only Starter files above one part (90–100 MB) need more than one request. How the browser saves such a
  file as one download (viewer assembles `Range` parts, or numbered parts) is OPEN (ARC-008, FE).
- Burn: `Burn:MaxRequests` (16) must exceed the part count of the largest burnable file plus retries; it does for
  Phase 1 sizes.
- Tickets appear as paths in edge logs; acceptable because they are short-lived and grant-bound.
- No Cloudflare feature may set a cookie on the host or on `.zaa.gl` (verified before launch, OPS-001/OPS-010).
- If Cloudflare's terms do not allow it, the product owner decides the delivery host again.

## Rejected options
- DNS-only host: exposes the origin address and loses the WAF.
