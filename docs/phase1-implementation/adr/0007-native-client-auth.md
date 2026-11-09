# ADR-0007: CLI and desktop sign in with device authorization; API keys stay paid-only

Status: Accepted 2026-11-09 (PD-07). Addendum §14. Contract C11.

## Context
API keys are Starter-only (spec §7.4) and their scopes are not enforced today. The PRD wants 30% of active users on the
desktop app or CLI, most of them Free.

## Decision
- OAuth 2.0 Device Authorization Grant (RFC 8628) for every account, CLI and desktop; approval on
  `app.zaa.gl/activate` with any sign-in method.
- Native tokens never carry Admin and are excluded from the Interactive policy (keys, billing, deletion, export).
- API keys stay paid-only (`ApiKeys` capability), for CI and automation; scopes enforced (BE-001); new scopes
  `snippets:write`, `files:write`, `resources:read`.
- Loopback + PKCE for desktop is a later upgrade.

## Consequences
- One backend flow works over SSH and in containers.
- Device sessions are listed and revocable in web settings.
- Open: headless CLI credentials, minimum client versions (PD-33).

## Rejected options
- Scoped API keys on Free: long-lived secrets on every laptop, contradicts the paid-keys rule.
- Paid keys only: Free users could not use the CLI or desktop app.
