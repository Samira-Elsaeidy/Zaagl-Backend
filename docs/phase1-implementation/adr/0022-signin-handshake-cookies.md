# ADR-0022: Sign-in handshake cookies allowed on api.zaa.gl for Google/GitHub routes only

Status: Accepted 2026-11-09 (PD-46, product owner). Addendum §4.1.2. Contract C3. Amends spec v9.4 §1.2. Extends ADR-0018.

## Context
Spec §1.2 says `api.zaa.gl` sets no cookies, but the OAuth handshake with Google and GitHub needs a short-lived external
sign-in cookie and correlation/nonce cookies (`Program.cs:150-179`).

## Decision
- Only `/api/auth/external/{provider}`, `/api/auth/external/callback`, `/api/auth/signin-google` and
  `/api/auth/signin-github` may set cookies, and only those the handshake needs.
- Each is host-only (no `Domain`), `HttpOnly`, `Secure`, at most 10 minutes, removed when the callback completes.
- No other `api.zaa.gl` route sets a cookie; no route accepts a cookie as an API credential (bearer-only stays).
- Spec §1.2 is amended for these routes only.

## Consequences
- `usercontent.zaa.gl` never receives these cookies (host-only), so ADR-0003 holds.
- A test asserts no `Set-Cookie` from any other `api.zaa.gl` route.

## Rejected options
- Moving the provider challenge to the BFF: more work for no security gain once the cookies are host-only and short-lived.
