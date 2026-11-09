# ADR-0001: Private content links carry a 128-bit key in the URL fragment

Status: Accepted 2026-11-09 (PD-01, product owner). Addendum §5.1. Contracts C1, C4.

## Context
Short codes are about 30 bits and are not secrets (spec §1, §5.3); once a length fills, roughly 1 in 100 random
5-character strings is a live code. Snippets (logs, `.env` files) and files need an unguessable URL. The PRD wants every
share to be a normal `zaa.gl` link.

## Decision
- Private links are `https://zaa.gl/<code>#k=<key>`: 16 CSPRNG bytes, base64url (22 characters).
- The server stores only SHA-256 of the key and compares in constant time; the plain key is returned once.
- On by default for snippets and files; the owner may choose public. Images may be public (default settled by
  ADR-0014: images are keyed by default too).
- Clients send the key in header `X-Zaagl-Link-Key`. A missing or wrong key answers exactly like an unknown code (404).

## Consequences
- The key never reaches access logs, `Referer`, crawlers or the `zaa.gl` redirect (browsers keep the fragment across
  the 302). Secret content does not unfurl in chat apps, by design.
- The redirect cannot check the key; the viewer API must (it is reachable directly anyway).
- The fragment can later carry Phase 2 client-side encryption keys.
- Resolved by PD-37 (ADR-0014): media is keyed by default; the owner may make an image public. Still open: whether
  non-image media (video, recordings) may be made public.

## Rejected options
- Code only: guessable at scale, contradicts "a code is never an access token".
- Key in the path: leaks into logs and `Referer`.
- Long ids under `zaa.gl/f/<id>`: a second namespace, loses aliases and the shared gates.
