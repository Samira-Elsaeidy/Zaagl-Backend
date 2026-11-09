# Stage 1 review: backend fix round (PD-36, R1, R2, auth limit, AZ-01, RD-01)

Reviewer: zaagl-head. Date: 2026-11-09. Checkout `C:\Users\pc\Zaagl-Backend`, HEAD `9923526`, index empty, nothing committed. Read from the diff and the tests; no build or test run by the reviewer (a second backend agent is building in the same checkout).

## Verdicts

| Item | Verdict | Owner |
|---|---|---|
| PD-36 bad credentials on `POST /api/links` → 401, never guest | ACCEPT WITH NOTES | backend |
| R1 `Interactive` on `/api/auth/me`, `/api/auth/logout-everywhere`, `/api/billing/me` | ACCEPT | – |
| R2 exhaustive scope test (`ApiScopeTests` over real endpoints) | ACCEPT WITH NOTES | backend |
| Auth limit 10/min (spec §7.4) | ACCEPT (`RateLimitOptions.AuthPerMinute = 10`, `appsettings.json` 10) | – |
| AZ-01 enrol-only token refused outside TOTP enrolment | ACCEPT | – |
| RD-01 HEAD never counts | **REWORK**: the fix opens a click-limit bypass (below) | backend |

Out of scope here (in progress by the next backend agent, not reviewed): `StatusPages.cs`, the 410 branches in `ResolutionEndpoints.cs` and the new `ResolveKind.Deleted/Disabled` (PD-39/PD-42, F-09), `HubTicketAuthentication.cs` (PD-41). The lead's reading of PD-42 matches its text exactly ("fallback wins for expired; disabled and deleted always 410"); no disagreement.

## AZ-01: can the enrol-only check be bypassed?

`Program.cs` `OnTokenValidated` fails the JWT when the token carries `TokenService.EnrolOnlyPurposeClaim` and the selected endpoint lacks `EnrolOnlyTokenAllowedMetadata`; only `/totp/enroll` and `/totp/confirm` carry it (`.AllowEnrolOnlyToken()`).
- It runs in JWT validation itself, so it covers every policy at once: default `RequireAuthorization()`, `Interactive`, scope policies, the SignalR hub (query-string token goes through the same handler). The API-key scheme cannot carry the claim.
- Endpoint selection happens before authentication (`WebApplication` routing runs first; `UseAuthentication` is at `Program.cs:251`), so `GetEndpoint()` is set. When no endpoint is selected the check **fails closed** (metadata null → refused).
- Guard against widening: `ApiScopeTests` asserts that exactly those two routes carry the metadata.
- Positive path tested over HTTP (QA `AuthRoutesTests`, backend `CredentialRulesTests`); negative path through QA's matrix (25/25 for `Defect=AZ-01|RD-01`, agent evidence).
- No bypass found.

## PD-36: notes

`CredentialRules.RejectFailedCredentials()`: if a request carries `Authorization` (any scheme) or the API-key header and is not authenticated, the answer is a 401 challenge; no header at all stays guest. Fails closed for unknown schemes (`Basic …` → 401). Applied to `POST /api/links` before `RequireScopeWhenAuthenticated`.
- Note (architect + backend): every Phase 1 guest-capable endpoint (snippet create, upload session create) must use the same filter. Add it to contracts.md (C9) and make `ApiScopeTests` require `RejectFailedCredentials` on every public-list route that also accepts credentials (today only `POST /api/links/`), so a future guest endpoint cannot forget it.

## R2: public allow-list, route by route

| Route | Justified? |
|---|---|
| `GET`/`HEAD /{slug:redirect}` | yes, public redirect |
| `/health`, `/health/live`, `/health/ready` | yes for Docker/deploy probes; must be denied at the edge (DEVOPS_PLAN §3.1, OPS-010) |
| `GET /health/issuer` | yes internally, but it exposes the creation-gate state: include it in the edge deny (OPS-010) |
| `GET /account/confirm-email`, `/account/reset-password` | yes, static pages, token in the fragment |
| `POST /api/auth/register`, `resend-confirmation`, `forgot-password`, `login` | yes, pre-authentication (rate-limited by `auth`) |
| `POST /api/auth/confirm-email`, `reset-password`, `refresh`, `logout` | yes, the credential is the token in the body |
| `GET /api/auth/external/{provider}`, `/external/callback` | yes, social sign-in (callback changes under PD-41) |
| `POST /api/webhooks/email`, `/api/billing/webhook` | yes, signature-gated (QA still to test unsigned → 401) |
| `POST /api/unlock/{slug}` | yes, visitor password unlock (rate-limited `unlock`) |
| `GET /api/public/stats/{slug}` | yes, owner opt-in public stats |
| `POST /api/reports` | yes, spec §7.15 anyone may report |
| `POST /api/links/` | yes, guest creation; scope enforced when authenticated and PD-36 filter present |

Every other endpoint must carry a scope, `Interactive` or `Admin` policy, and `AllowAnonymous` outside the list fails the test; stale entries fail too; the enumeration asserts > 50 endpoints so an empty host cannot pass. Good. Note: `RealApiFactory` removes all hosted services, which is correct for a metadata-only test.

## RD-01: REWORK

`ResolveAsync(..., countsAsClick: false)` for HEAD returns `Redirect` to the destination after the gate, **without looking at the click budget** (the agent states this). Before the fix, HEAD consumed a click (the defect) but an exhausted link answered with the fallback. Now:
- a click-limited link that is already used up still reveals its destination to anyone who sends `HEAD` (`curl -I`), indefinitely. For "one-time" links (`MaxClicks = 1`), the whole point of the limit is lost.

Exact fix (backend): on the HEAD path, for links with `MaxClicks`, do a read-only check of the remaining budget (`click_count >= max_clicks`, no update, no lock; a stale read is acceptable for HEAD) and return the same `Expired` result a GET would (fallback per PD-42, else the 410 page). Add a test to `HeadNeverCountsTests`: exhaust a `MaxClicks = 1` link with one GET, then HEAD must not return the destination in `Location`. Whether HEAD may show the destination **while** clicks remain is a product question (Q-H1).

## Other observations

- `tests/Zaagl.Links.Tests/vectors.json` shows as modified, but `git diff` shows no content change (line endings only; rewritten at 14:36 by a test run, probably `VectorGenTrace`). The committed code vectors must never change: the lead should `git checkout -- tests/Zaagl.Links.Tests/vectors.json` before committing, and backend should stop that test writing into the source tree (write to the output folder instead).
- The agent's own test left an Admin user in the shared DB and caused one transient failure, fixed. Fine under the PD-24 deferral; QA records it.
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 added to `Zaagl.Links.Tests` (test-only), reasonable.
- Results are agent evidence: build 0 errors, focused 49/49, `Zaagl.Links.Tests` 345/345, QA suite 485/513 (remaining 28 explained: 3 PD-39, 20 PD-40, 1 F-09, 4 stale R1 expectations, all owned by the in-progress agents and QA). Full solution and CI not yet run.

## Question for the user

- **Q-H1. Should a "peek" (a HEAD request, used by link checkers and chat previews) reveal where a click-limited link goes, while it still has clicks left?** Options: (a) yes, as the spec says HEAD answers like GET without counting; a one-time link can then be peeked without using it up; (b) no: for links with a click limit, HEAD never shows the destination (link checkers just see "a redirect"). In both cases a used-up link never reveals it (rework above). Recommendation: (b). Click limits are used for one-time or limited sharing, and a silent peek defeats that; ordinary links are unaffected.
