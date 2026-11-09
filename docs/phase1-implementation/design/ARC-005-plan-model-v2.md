# ARC-005 · Plan model v2: content limits and capability flags (no Pro)

| | |
|---|---|
| Owner | zaagl-architect. Implementer: backend (BE-003, Billing owner; one change each in Links and Identity). |
| Status | DESIGN, ready to implement. Contract C5 frozen (`src/Modules/Billing/Contracts/ContentPlanContracts.cs`, `BillingStatusDto.Capabilities` added this round). |
| Rules | Addendum §10, §11, §14.3, §16.14. PD-04 APPROVED (changed): Free and Starter only, **no Pro**. PD-07. ADR-0004. |
| Supersedes | ARCHITECTURE.md §6 ARC-005 text ("Add Pro", "5 GB uploads", Stripe price → tier for Pro): rejected by PD-04. |

## 1. Model

- `PlanTier { Free, Starter }` stays as is (`src/Modules/Billing/Contracts/BillingContracts.cs:3-7`). No new value.
- `PlanLimits` keeps its Phase 0 behaviour for URL links (`BillingContracts.cs:16-34`).
- New, already stubbed: `PlanCapabilities`, `ContentLimits`, `ContentPlan`, `IContentPlanProvider` (C5).
- Rule (addendum §10.2, §16.14): outside the Billing module no code compares `PlanTier` values or reads
  `PlanLimits.Tier` / `ContentPlan.Tier` for a decision. It reads a capability flag or a `ContentLimits` value.
  `Tier` stays on the DTOs for display only.

## 2. Configuration

Values live in configuration, validated at startup (`ValidateOnStart`), never hard-coded. Sizes are integers in bytes,
and 1 MB = 1,048,576 bytes (PD-54); every value below is approved (PD-04, PD-54, PD-56).

```jsonc
"Plans": {
  "Guest":   { "Content": { "MaxFileBytes": 26214400,  "MaxSnippetBytes": 262144, "RetentionDays": 7,  "QuarantineAfterExpiry": false, "StorageQuotaBytes": null },
               "Capabilities": { "IsPaid": false, "ApiKeys": false, "KeepStatsAfterPurge": false, "BurnAfterDownload": false, "PasswordOnContent": false, "ContentStatistics": false, "ManageContent": false } },
  "Free":    { "Content": { "MaxFileBytes": 26214400,  "MaxSnippetBytes": 524288, "RetentionDays": 7,  "QuarantineAfterExpiry": true,  "StorageQuotaBytes": 262144000 },
               "Capabilities": { "IsPaid": false, "ApiKeys": false, "KeepStatsAfterPurge": false, "BurnAfterDownload": false, "PasswordOnContent": false, "ContentStatistics": true,  "ManageContent": true } },
  "Starter": { "Content": { "MaxFileBytes": 104857600, "MaxSnippetBytes": 2097152, "RetentionDays": 30, "QuarantineAfterExpiry": true,  "StorageQuotaBytes": 5368709120 },
               "Capabilities": { "IsPaid": true,  "ApiKeys": true,  "KeepStatsAfterPurge": true,  "BurnAfterDownload": true,  "PasswordOnContent": true,  "ContentStatistics": true,  "ManageContent": true } }
}
```

| Value | Guest | Free | Starter | Status |
|---|---|---|---|---|
| `MaxFileBytes` | 25 MB | 25 MB | 100 MB | APPROVED (PD-04; guest PD-54) |
| `RetentionDays` | 7 | 7 | 30 | APPROVED (PD-04, PD-14) |
| `QuarantineAfterExpiry` | false | true | true | APPROVED (PD-06) |
| `StorageQuotaBytes` | null | 250 MB | 5 GB | APPROVED (PD-54) |
| `MaxSnippetBytes` | 256 KB | 512 KB | 2 MB | APPROVED (PD-56) |
| `IsPaid`, `KeepStatsAfterPurge` | false | false | true | Phase 0 behaviour |
| `ApiKeys` | false | false | true | APPROVED (PD-07) |
| `BurnAfterDownload`, `PasswordOnContent` | false | false | true | APPROVED (PD-54: Starter only) |
| `ContentStatistics`, `ManageContent` | false | true | true | APPROVED (spec §7.12, addendum §11.9) |

Startup validation: every plan section present; `RetentionDays ≥ 1`; `0 < MaxFileBytes ≤ 104,857,600` (Phase 1 maximum,
keeps "too large to scan" impossible, addendum §7.1.4); guest `QuarantineAfterExpiry == false` and
`StorageQuotaBytes == null`; account plans have a non-null quota; every capability present.

## 3. Provider

`ContentPlanProvider : IContentPlanProvider` in `Billing.Application`, registered in `BillingModule`:
- `GetForUserAsync(userId)`: reads the tier the same way `PlanProvider` does
  (`src/Modules/Billing/Application/BillingService.cs:27-36`, one indexed query on `subscriptions`), maps tier to the
  config section inside Billing (the one allowed tier switch), returns `ContentPlan(tier, IsGuest: false, limits,
  capabilities)`. Cache per user for 60 seconds (`HybridCache`, local only), the same bound as the security-stamp
  cache; the webhook handler removes the entry on a subscription change.
- `Guest`: the `Plans:Guest` section, built once.
- Plan options are bound with `IOptionsMonitor`, so a configuration change applies without code.

## 4. Every tier comparison and what replaces it

Backend (grep `PlanTier\.|\.Tier\b` on the 2026-11-09 working tree, excluding Contracts and migrations):

| Site | Today | Replacement |
|---|---|---|
| `src/Modules/Links/Application/PurgeService.cs:102` | `isPremium = (await plans.GetLimitsAsync(ownerId, ct)).Tier == PlanTier.Starter` | Inject `IContentPlanProvider`; `isPremium = (await contentPlans.GetForUserAsync(ownerId, ct)).Capabilities.KeepStatsAfterPurge`. |
| `src/Modules/Identity/Application/IdentityService.cs:442` (`CreateApiKeyAsync`) | `if (limits.Tier != PlanTier.Starter) return Error.PlanRequired(...)` | `if (!(await contentPlans.GetForUserAsync(userId, ct)).Capabilities.ApiKeys) return Error.PlanRequired(...)`. Identity already references Billing.Contracts. |
| `src/Modules/Billing/Application/BillingService.cs:58` | `sub?.Tier == PlanTier.Starter` ("already on Starter") | Inside Billing: allowed. Keep. |
| `BillingService.cs:149` (`OnCheckoutCompletedAsync`) | `sub.Tier = PlanTier.Starter` | Inside Billing: allowed. Map the checkout's price id to a tier through `Stripe:PriceTiers` (`{ "<priceId>": "Starter" }`, today one entry from `StarterPriceId`, `BillingService.cs:21`); unknown price → log and ignore. No Pro price exists. Invalidate the provider cache. |
| `BillingService.cs:164` (`OnSubscriptionChangedAsync`) | `entitled ? Starter : Free` | Same price → tier mapping; invalidate the cache. |
| `BillingService.cs:33-35, 49` | reads tier to build `PlanLimits` | Inside Billing: allowed. `GetStatusAsync` (`:46-51`) fills the new `BillingStatusDto.Capabilities`. |

Gap found (not a tier comparison, but the same rule): API keys keep working after a downgrade. Spec §7.4 and addendum
§14.3 say keys stop working when the plan loses `ApiKeys`; `IdentityService.ValidateApiKeyAsync`
(`IdentityService.cs:603-614`) checks only hash and revocation, and `ApiKeyAuthenticationHandler`
(`src/Zaagl.Api/Infrastructure/ApiKeyAuthentication.cs:24-29`) trusts its result. Fix in BE-003: after the key matches,
read `Capabilities.ApiKeys` (60-second cache, §3); false → authentication fails → `401` with `WWW-Authenticate`
(PD-36 lists "key of a plan without ApiKeys" as a failing credential). Spec §7.4: on a downgrade that removes
`ApiKeys` every key of the user is **revoked** at once and a security notice is sent; an upgrade does **not** restore
revoked keys (the user creates new ones). Built (BE-003): Billing calls `IPlanChangeListener` after its webhook commit
(`src/Modules/Billing/Application/BillingService.cs:214-235`); Identity's `ApiKeyPlanChangeListener` sets `RevokedAt`
on every active key and queues the notice (`src/Modules/Identity/Application/ApiKeyPlanChangeListener.cs:8-11`,
`IdentityService.cs:473-490`). The authentication-time plan check (`IdentityService.cs:629-632`) is the backstop when
revocation did not run. `IPlanChangeListener` is a post-freeze C5 change: contracts.md change log F-1 (rationale and
the "after commit, not one transaction" deviation from spec §8.2, OPEN).

Frontend (informational, FE owner): `src/components/app-shell.tsx:119` (`tier === 1`), `src/features/account/api.ts:22`
(`tier !== 1`), `src/features/account/SettingsView.tsx:83` (`isStarter`). Display of the plan name may keep the tier;
feature gating (API key page, upgrade hints, content options) reads `capabilities` from `GET /api/billing` once BE-003
fills it.

Phase 0 `PlanLimits` reads that stay (URL-link rules, not tier comparisons): `LinkService.cs:96` (`MonthlyLinks`),
`:545, :551` (`DefaultExpiryDays`, `MaxExpiryDays`), `:924-927` (`CustomSlugs`, `AdvancedGates`),
`src/Zaagl.Api/Endpoints/AnalyticsEndpoints.cs:25,55,73,87` (`AnalyticsRetentionDays`).

## 5. Enforcement test

A source test (Billing excluded) fails on: `PlanTier.` used in `==`/`!=`/`switch`/pattern outside
`src/Modules/Billing/**`; `.Tier ==`, `.Tier !=`, `.Tier is`. Test helpers that construct `PlanTier.Starter` fakes
(`tests/**/FixedPlanProvider`) are allowed. This is QA/ARC-017 work (deferred by PD-51); BE-003 leaves a grep in CI
or a comment pointing to it.

## 6. Where content code reads plan values

| Rule | Reader | Value |
|---|---|---|
| Upload create, simple upload: size | Files | `Limits.MaxFileBytes` → `413 file-too-large` |
| Quota reservation | Resources (`IContentQuota`) | `Limits.StorageQuotaBytes` (null = no ledger: guests) |
| Snippet create | Snippets | `Limits.MaxSnippetBytes` → `413 snippet-too-large` |
| Expiry clamp, renewal | Resources | `Limits.RetentionDays` → `400 expiry-beyond-plan` |
| Purge rule (quarantine or not) | Resources → Links issuer | `Limits.QuarantineAfterExpiry` (guests false) |
| Password on content | Resources | `Capabilities.PasswordOnContent` → `402 plan_required` |
| Burn after download | Resources | `Capabilities.BurnAfterDownload` → `402 plan_required` |
| Content statistics recorded | Resolution, Resources, Files | `Capabilities.ContentStatistics` |
| Renew / edit / delete content | Resources | `Capabilities.ManageContent` |
| Keep stats at purge | Links purge | `Capabilities.KeepStatsAfterPurge` |
| Create and use API keys | Identity | `Capabilities.ApiKeys` |

Checks run after the idempotency claim and inside the creating transaction (addendum §10.4, spec MUST NOT #8).

## 7. Downgrade (to confirm, addendum §9.7)

Interim behaviour that invents nothing: existing content keeps its expiry and bytes; new content uses the new plan;
quota reservations check the new quota, so a user above it cannot upload until usage drops; API keys are revoked
and stay revoked after a later upgrade (§4). Renewal after a downgrade is capped by the new plan's `RetentionDays`. Q3 asks the user to
confirm.

## 8. Migration plan

No database migration (capabilities and limits are configuration; `subscriptions.tier` is unchanged). Deploy order:
configuration with the new `Plans` section → BE-003 code → FE reads `capabilities`. Stripe configuration list for
Phase 1: `Stripe:SecretKey`, `Stripe:WebhookSecret`, `Stripe:StarterPriceId` (existing, `BillingService.cs:19-21`), new
`Stripe:PriceTiers` map with the one Starter price. Nothing for Pro.

## 9. Review checklist (BE-003)

- [ ] No `PlanTier` comparison outside Billing (grep clean).
- [ ] Purge archives stats for a Starter owner and deletes them for Free (existing `PurgeJobTests` pass unchanged).
- [ ] API key creation refused for Free with `402 plan_required`; on a downgrade every key of the user is revoked and a
      security notice is queued; a key gets `401` (at the latest within the 60-second plan cache if revocation did not
      run); after an upgrade the revoked keys stay revoked (spec §7.4).
- [ ] Startup fails on a missing or invalid `Plans` section.
- [ ] `GET /api/billing` returns `capabilities`.

## 10. Questions for the user

- Q1 Answered by PD-54: Free 250 MB, Starter 5 GB; guest file cap 25 MB; burn and password on content Starter only.
- Q2 Answered by PD-54: 1 MB = 1,048,576 bytes. Snippet caps answered by PD-56 (guest 256 KB, Free 512 KB, Starter 2 MB).
- Q3 Downgrade from Starter to Free: keep existing content until its own expiry (recommended, §7), or shorten it?
