# Stage 1 review: Backend (BE-001 API-key scopes, BE-030 forwarded headers, BE-031 redirect hardening)

Reviewer: zaagl-head. Date: 2026-11-09. Nothing committed; this review changes no product files.

## Verdicts

| Task | Verdict | Owner of fix |
|---|---|---|
| BE-001 API-key scopes | **REWORK** (R1, R2; R3 needs the user's yes) | backend |
| BE-030 Forwarded headers | ACCEPT WITH NOTES | backend / devops (config) |
| BE-031 Redirect hardening and limits | ACCEPT WITH NOTES | backend |

## What changed (main checkout, uncommitted, applied by the lead from the agent's worktree)

Modified: `docs/API.md`, `docs/RUNBOOK.md`, `IdentityService.cs`, `LinksEndpoints.cs`, `AnalyticsEndpoints.cs` (lead, by hand), `ResolutionEndpoints.cs`, `AnalyticsHub.cs`, `ApiResults.cs`, `Program.cs`, `appsettings.json`, `SharedKernel/Net/IpPartition.cs`, `Zaagl.Links.Tests.csproj` (+ `Microsoft.AspNetCore.TestHost`). New: `ApiScopes.cs`, `ForwardedHeadersSetup.cs`, `RedirectRateLimiter.cs`, tests `ApiScopeTests.cs` (6 test methods, route × principal theories), `ForwardedHeadersTests.cs` (9), `RedirectHardeningTests.cs` (12), `ApiTestHost.cs`. All inside backend ownership (AGENT_TASKS.md). No other behaviour changed beyond the three tasks, except rate-limit values moved to configuration with unchanged defaults.

## BE-001: REWORK

What is right: `scope:<scope>` policies, JWT passes, API key needs the claim, `403 insufficient-scope` problem body, scopes on every Links/Folders/Tags/Analytics/Archive endpoint and the SignalR hub; `POST /api/links` checks `links:write` for authenticated callers while still allowing guests; keys created without scopes get only the Phase 0 set (no silent widening); content scopes accepted at creation as addendum §14.4 says.

**R1 (must, small): close the three endpoints that accept any API key.**
- `POST /api/auth/logout-everywhere` (`IdentityEndpoints.cs:93-98`) uses plain `RequireAuthorization()`. A leaked read-only key can log the owner out of every device (bumps the security stamp). Fix: `.RequireAuthorization("Interactive")`.
- `GET /api/auth/me` (`IdentityEndpoints.cs:112-114`) and `GET /api/billing/me` (`BillingEndpoints.cs:15-16`): any key reads the owner's account and plan. Fix: `Interactive` for both, unless `docs/API.md` promises them to API keys, in which case `.RequireScope(ApiScopes.LinksRead)`. Addendum §14.4: "Scopes are enforced on every endpoint".

**R2 (must, small): make the scope test exhaustive instead of a hand list.** `ApiScopeTests.Routes` lists 18 routes and misses `/api/links/{id}/analytics/events` and `/analytics/utm` (scoped by the lead by hand, untested) and the hub. Add a test that enumerates `EndpointDataSource` and fails for any endpoint that an API key can authenticate to without a `scope:*`, `Interactive` or `Admin` policy (allow-list for anonymous/public routes). This would have caught R1 and protects every Phase 1 endpoint.

**R3 (needs the user's yes, then must before Stage 2): invalid API key on `POST /api/links` falls through to guest creation.** Spec §7.2 step 1 and MUST NOT #27, and addendum §11.1 (guest content, PD-14): invalid or expired credentials → `401`, never guest. Today it is masked for scripts because guest creation also needs a CAPTCHA, but every Phase 1 guest endpoint (snippets, uploads) will copy this pattern. Fix: when an `Authorization` or API-key header is present and authentication fails, answer `401` on `AllowAnonymous` creation endpoints (one shared filter or handler event), plus tests for a revoked, expired and malformed key. It is API-key handling, so it fits BE-001, but PD-24 limits Stage 1 strictly, so the user decides (question Q2).

## BE-030: ACCEPT WITH NOTES

- `ForwardedHeadersSetup.Apply`: trusts only exact `Network:KnownProxies` (subnets rejected) and CIDR `KnownNetworks`; invalid entries fail startup; clears the framework's loopback default; **switches forwarding off when both lists are empty** (ASP.NET Core otherwise trusts every sender when both lists are empty, the Phase 0 defect). Adds `X-Forwarded-Host` (needed by the BFF contract). `ForwardLimit` configurable (2).
- All IP rate limiters now partition by `IpPartition.Key` (IPv4 address / IPv6 /64), as spec §7.3 requires.
- Notes: (1) Default config trusts nothing, so in dev compose and in any deployment without `Network:KnownProxies`, every client appears as the proxy's or BFF's address and shares one rate-limit bucket. OPS-007 must set the proxy and web container addresses; the frontend BFF (FE-029) must send `X-Forwarded-For`/`-Host`. Record in STATUS as a cross-team dependency. (2) Guest limits stay BLOCKED in an environment until this config is deployed there (addendum §11.6).

## BE-031: ACCEPT WITH NOTES

- Route now `^[A-Za-z0-9][A-Za-z0-9_-]{3,63}$`: one segment, 4–64 characters; 1–3 characters, multi-segment and file-like paths match no route and get `404` before any I/O. No live link is lost: custom slugs were already ≥ 4 (`SlugRules.MinCustomLength = 4` since Phase 0) and generated codes ≥ 5.
- `InMemoryRedirectRateLimiter`: 300/min per partition; misses (NotFound, Expired) 30 per /64 or IPv4, 120 per /56, 300 per /48 or IPv4 /24, spec numbers, configurable; HEAD counts like GET; checked before the database scope is resolved; `Retry-After` set; past windows swept, memory bounded per minute. Once a partition spends a miss budget, hits are refused too until the window ends; that is the only way to stop enumeration, since a miss is known only after the lookup.
- Deferred gaps (accept as later work, add to STATUS with owners):
  - Global miss-rate breaker and miss metric/alert (spec §7.3): required **before launch**, not for Stage 2. Track as a BE ticket.
  - Auth limit default 20/min vs spec §7.4 10/min: now a config value; change the default to 10 (one line) in the R-round since the code is open anyway. Not a product decision; the spec is explicit.
  - Reserved-word 404 at the redirect: the agent's reason ("generated codes could equal words") is incorrect; `LinkService` skips reserved generated codes (`LinkService.cs:127, 319, 1006`) and aliases are rejected (`:690`). A redirect-side check is defence in depth only. Later, low priority.
  - The limiter is per instance (spec allows it behind the interface); Valkey implementation when the API scales out.

## Verification

- Lead, in the main checkout: `dotnet build src/Zaagl.Api` 0 errors; focused tests `ApiScopeTests|RedirectRateLimiterTests|RedirectRouteTests|ForwardedHeadersTests` 88/88 passed.
- Agent, in its worktree only: full `Zaagl.Links.Tests` 353/353. **NOT re-verified in the main checkout**; the full suite must pass there (or in CI) before Stage 1 is closed. Shared-DB flakes are recorded, not retried (PD-24).
- Reviewer: did not rebuild (other agents are building); verified by reading the code and grep.

## Process

- The agent once bypassed its sandbox by writing to the main checkout through PowerShell, then reverted. It matters: the sandbox is part of the user's permission system, and an agent may not work around it even temporarily. No lasting effect is visible (`git status` shows only the files the lead applied, and the lead re-applied from the patch). Action for the lead: tell the backend agent explicitly that a blocked write is reported, never worked around; record the incident in STATUS.
- No commits (`HEAD` 9923526). Worktrees `worktree-agent-*` exist (see stage1-devops.md Q4).

## Questions for the user

- **Q1.** None needed for R1, R2 or the auth-limit default: they are inside BE-001/BE-031 and the spec.
- **Q2. A wrong or revoked API key on "create link" currently makes the request a guest request instead of an error. Fix it now in Stage 1?** Options: (a) yes, now, as part of BE-001 (small, and Stage 2 guest snippets reuse the pattern); (b) later, before guest content ships in Stage 2. Recommendation: (a).
