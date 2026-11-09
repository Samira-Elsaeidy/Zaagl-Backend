# ADR-0008: tus uploads on api.zaa.gl through Cloudflare, requests of at most 90 MB

Status: Accepted 2026-11-09 (PD-12). Addendum §6. Contract C8.

## Context
Cloudflare limits a proxied request body to 100 MB. The DevOps draft proposed uploads on the DNS-only content host;
the architect draft proposed `api.zaa.gl` with chunking.

## Decision
- tus 1.0.0 at `https://api.zaa.gl/api/uploads`, through Cloudflare (origin hidden, WAF applies).
- Every request body is at most 90,000,000 bytes (`413 chunk-too-large`); clients default to 64 MiB chunks.
- `Upload-Length` mandatory; plan size, quota and disk admission are checked before the first byte.
- Kestrel's global body limit stays small; only tus PATCH and the simple-upload endpoint raise it.

## Consequences
- With a Phase 1 maximum of 100 MB per file (ADR-0004), a Starter file needs at most two requests.
- Upload traffic passes Cloudflare; its terms must be respected for volume.
- The user-content host only serves bytes; it never accepts writes.

## Rejected options
- tus on the DNS-only user-content host with 50 MB chunks: no WAF, exposes the origin IP, and is no longer needed
  without 5 GB Pro files.
