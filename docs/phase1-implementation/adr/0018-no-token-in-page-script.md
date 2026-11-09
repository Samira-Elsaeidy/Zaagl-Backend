# ADR-0018: One-time sign-in code and hub ticket instead of tokens in page script

Status: Accepted 2026-11-09 (PD-41, product owner). Addendum §4.3, §4.5. Contract C11. Completes ADR-0011.

## Context
ADR-0011 moves the web session to the BFF so browser script never holds a token. Two places still hand one to page
script: the social sign-in callback redirects to `app.zaa.gl/auth/callback#token=…&refresh=…`
(`IdentityEndpoints.cs:153`), and the statistics hub takes the access token from the `access_token` query parameter
(`Program.cs:118-123`). Phase 1 renders untrusted content in the app origin, so one XSS would capture them.

## Decision
Before Stage 2 content goes live:
- **Social sign-in:** the provider callback redirects to the BFF with a one-time sign-in code, never a token in the URL.
  The BFF redeems it server-side for the token pair and stores it in `__Host-zaagl_session`. The code is single-use,
  stored hashed, short-lived (to confirm, proposed 60 s) and bound to a verifier the BFF holds from the start of the
  flow, so a code taken from a URL or log cannot be redeemed elsewhere.
- **Live statistics:** the BFF obtains a separate short-lived hub ticket server-side and gives only that to page
  script. It is valid only to connect to the statistics hub, bound to one user, single-use at connect (lifetime to
  confirm, proposed 60 s). The hub then stops accepting access tokens in the query string, and closes connections of a
  revoked session within the spec's 60-second security-stamp bound.

## Consequences
- A script injected into `app.zaa.gl` can at most open a statistics connection, not act as the user on the API.
- Route names and ticket format: backend with ARC-016.
- The provider handshake cookies on `api.zaa.gl` are allowed on the sign-in routes only (PD-46, ADR-0022).

## Rejected options
- Tokens in the URL fragment: readable by any script on the callback page and kept in history.
- Access token in the hub query string: a full API credential in page script and in edge logs.
