# ADR-0004: Phase 1 plans are Free and Starter; checks use capability flags

Status: Accepted 2026-11-09 (PD-04, changed: no Pro tier). Addendum §10. Contract C5.

## Context
The PRD lists Free, Starter and Pro (5 GB, "permanent"); Billing code has only Free and Starter and compares
`Tier == Starter` in several places (`PurgeService.cs:102`, `IdentityService.cs:395`, `BillingService.cs:58,149,164`).

## Decision
- Phase 1 has Free and Starter only. Per file and retention: Free 25 MB / 7 days, Starter 100 MB / 30 days.
- Per-account storage totals: proposed Free 250 MB, Starter 5 GB, **to confirm**.
- New `ContentLimits`, `PlanCapabilities` and `IContentPlanProvider` in `Billing.Contracts`; `PlanLimits` keeps its
  Phase 0 behaviour. Code outside Billing reads flags, never compares tiers (grep test).

## Consequences
- No 5 GB uploads; the largest file (100 MB) stays under ClamAV limits and under the Cloudflare request limit with
  chunking, so "too large to scan" does not exist in Phase 1.
- The PRD exit gate "3% free-to-Pro" cannot be measured until Pro exists.
- Adding Pro later is a configuration and enum change, not a rewrite.
- Open: per-plan values for burn, password on content, snippet caps (PD-17), storage totals.

## Rejected options
- Add Pro in Phase 1 (draft recommendation): rejected by the product owner; "permanent" 5 GB files were also unbounded
  in capacity on one VPS.
