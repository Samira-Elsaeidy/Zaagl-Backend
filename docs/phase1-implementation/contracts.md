# Phase 1 contract pack (C1–C12)

| | |
|---|---|
| Owner | zaagl-architect (ARC-001) |
| Status | **Contract freeze reached 2026-11-09** (ARC-001, PD-51). Every contract C1–C12 is stubbed in a `Contracts` folder or, for the host/API contracts C3 and C9, specified in this document, `design/ARC-008-usercontent-delivery.md` and `design/openapi-phase1.yaml`. Stubs build per module with 0 errors. One frozen change is applied by BE-007, not by the stub: `DestinationUrl` becomes nullable on `LinkSnapshot`/`LinkDto`/`AdminLinkDto` (flipping it earlier only adds nullable warnings in files the backend is editing). Changes after the freeze follow the rule below. |
| Design notes | `design/ARC-002` link target, `ARC-004` access gate, `ARC-005` plans, `ARC-006` lifecycle and quota, `ARC-008` delivery, `ARC-012` jobs (PD-25 APPROVED), `ARC-014` native auth, `openapi-phase1.yaml` (ARC-016); CSP: `adr/0023-app-csp-strategy.md` (APPROVED, PD-53) |
| Normative behaviour | [docs/spec/zaagl-phase1-addendum.md](../spec/zaagl-phase1-addendum.md) ("addendum", v1.4, APPROVED as the Phase 1 rulebook, PD-34) |
| Decisions | [DECISIONS.md](DECISIONS.md) approvals of 2026-11-09 (PD-14 as updated, PD-15, PD-25, PD-28, PD-34, PD-36 to PD-49, PD-51, PD-53 to PD-57; PD-52
  deferred); one ADR each in [adr/](adr/) |
| Supersedes | ARCHITECTURE.md §5 contract table where they differ (Pro tier removed, `<ucd>` replaced by `usercontent.zaa.gl`, upload host fixed) |

## Rules for every contract

- Modules talk only through `Contracts` namespaces. The arrows below are "references the Contracts of"; no reverse
  references. Today Resources, Files, Snippets and Media reference only SharedKernel, so their stubs use plain `Guid`
  ids and mirror enum numbers instead of referencing each other. Adding project references (Snippets/Files/Media →
  Resources, Media → Files, Resources → Links/Access/Billing) is part of BE-006 and needs no contract change.
  Because the enums are duplicated, an architecture test MUST assert that `LinkTargetKind` (minus `Url`) and
  `ResourceKind` have identical names and values (QA/ARC, Head review).
- Stubs contain types and interfaces only: no implementation, no DI registration, no endpoint, no migration.
- Time: every implementation reads `TimeProvider`; `DateTime` values are UTC.
- Errors: `Result<T>` / `Error` from SharedKernel; problem `type` URIs from BE-002.
- A contract change after freeze needs the owner's PR plus architect review; consumers are named so they can be asked.

### Changes after the freeze (change log)

| # | Date | Contract | Change | Owner / consumers | Status |
|---|---|---|---|---|---|
| F-1 | 2026-11-09 | C5 | New `IPlanChangeListener` in `Billing.Contracts` (`src/Modules/Billing/Contracts/BillingContracts.cs:41-51`). Details in C5 notes. | Billing (owner); Identity (`ApiKeyPlanChangeListener`) | Architect-reviewed (Head final review A1); deviation from spec §8.2 pending Q1 of `reviews/stage1-final.md` |
| F-2 | 2026-11-09 | C1 / C4 | The link key is minted by the Links issuer itself, not through Access's `ILinkKeys`; the hashing format is fixed in C4 notes ("Link key format"). Description change only, no signature change. | Links (issuer), Access (verifier) | Recorded (Head final review A3) |
| F-3 | 2026-11-09 | C1 | Idempotent replay of `CreateContentLinkAsync` returns `LinkKey = null` (built behaviour); the earlier "rotate on replay" default is withdrawn; the product rule is OPEN (ARC-002 Q1). | Links; Resources, FE, CLI, Desktop | OPEN for the user (Head final review B4) |

```
Links  <-  Resources  <-  Snippets
  ^          ^   ^-----  Files  <-  Media
Access ------+   |
Billing ---------+
Analytics <- Resources, Files
```

## Summary

| ID | Contract | Owner | Consumers | Stub file |
|---|---|---|---|---|
| C1 | Content link target, `ILinkIssuer`, `IContentLinkCommands` | Links (backend) | Resources, Resolution, FE | `src/Modules/Links/Contracts/ContentLinkContracts.cs`, `LinkContracts.cs` |
| C2 | Resource envelope, registry, quota, kind handlers, delivery grants | Resources (backend) | Snippets, Files, Media, Api | `src/Modules/Resources/Contracts/ResourceContracts.cs` |
| C3 | Hosts, URL scheme, ticket URL format | Architect + DevOps | all | this document + addendum §4 |
| C4 | `IAccessGate`, `IAccessGrants`, link key, header names | Access (backend) | Resolution, Resources, Files, FE, CLI, Desktop | `src/Modules/Access/Contracts/AccessContracts.cs` |
| C5 | `ContentLimits`, `PlanCapabilities`, `IContentPlanProvider` | Billing (backend) | all backend | `src/Modules/Billing/Contracts/ContentPlanContracts.cs` |
| C6 | Analytics event kind | Analytics (backend) | Resolution, Resources, Files, FE | `src/Modules/Analytics/Contracts/AnalyticsContracts.cs` |
| C7 | Object storage v2 | Files (backend) | Media, DevOps | `src/Modules/Files/Contracts/FileContracts.cs` |
| C8 | Upload protocol (tus) | Files (backend) | FE, CLI, Desktop, QA | `src/Modules/Files/Contracts/FileContracts.cs` |
| C9 | Public content REST API | Architect review (ARC-016), module owners | FE, CLI, Desktop, QA | this document + `design/openapi-phase1.yaml` (draft; BE-028 generates the real one) |
| C10 | Link lifecycle events + outbox | Links + Resources | Files, Media, Abuse | `ContentLinkContracts.cs` |
| C11 | Native client auth and scopes | Identity (backend) | CLI, Desktop | `src/Modules/Identity/Contracts/NativeClientContracts.cs` |
| C12 | Content abuse: takedown, hash blocklist, evidence | Links (abuse) + Files | Admin, QA | `ContentLinkContracts.cs`, `FileContracts.cs` |
| — | Snippet and media view types | Snippets / Media (backend) | Api, FE, CLI | `SnippetContracts.cs`, `MediaContracts.cs` |

---

## C1 · Content link target

Owner: Links. Consumers: Resources (creates links), Resolution (302 to viewer), FE (library shows kind).
Decisions: PD-01, PD-02 (ADR-0001, ADR-0002). Addendum §2, §3.

```csharp
namespace Zaagl.Modules.Links.Contracts;

public enum LinkTargetKind : short { Url = 0, Snippet = 1, File = 2, Media = 3 }
public static class ViewerPathSegments { public const string Snippet = "p", Media = "i", File = "f"; }

public sealed record ContentLinkRequest(
    Guid ResourceId, LinkTargetKind Kind, Guid? OwnerId, byte[]? GuestCreatorKey, DateTime ExpiresAt,
    string? CustomSlug = null, string? Title = null, string? Password = null, bool RequireLinkKey = true);

public sealed record ContentLinkIssued(
    Guid LinkId, string Code, string ShortUrl, DateTime ExpiresAt, DateTime PurgeAfter, string? LinkKey);

public interface ILinkIssuer
{
    Task<Result<ContentLinkIssued>> CreateContentLinkAsync(ContentLinkRequest request, CancellationToken cancellationToken);
}
```

Notes
- Columns `target_kind smallint default 0`, `resource_id uuid null`, check `(target_kind = 0) = (resource_id IS NULL)`;
  `destination_url`/`target_host` nullable for content kinds. Expand-only migration; indexes `CONCURRENTLY`.
- Stubbed (trailing optional parameters, source-compatible): `LinkSnapshot` gains `TargetKind`, `ResourceId`,
  `LinkKeyHash`; `LinkDto` gains `TargetKind`, `ResourceId`. Applied by BE-007 (frozen here): `DestinationUrl` nullable
  on `LinkSnapshot`, `LinkDto`, `AdminLinkDto` (plus `AdminLinkDto.TargetHost`; `AdminLinkDto` gains kind and resource
  id); export rows get a nullable `TargetUrl` and a `Kind`; cache key `link:v2:{slugKey}`; `ResolveKind.Content`
  returns the viewer URL. Full site list: `design/ARC-002-link-target-model.md`.
- `IContentLinkCommands` (new): `IssueNewLinkKeyAsync`, `RemoveLinkKeyAsync`, `SetPasswordAsync`, `RenewAsync`,
  `DeleteAsync`; called by Resources for owner `PATCH/DELETE /api/resources/{id}`; each writes its C10 outbox row.
- Idempotent replay (change F-3). Built behaviour: the idempotency key is `content:{ResourceId:N}` in the owner or
  guest scope; a replay with the same request hash returns the stored `ContentLinkIssued` with `LinkKey = null`,
  because the stored response is written without the plain key (`src/Modules/Links/Application/LinkService.Content.cs:49-64`,
  `:91-95`, `:120-121`); a different request hash returns `422 idempotency-mismatch`. The issuer does **not** rotate
  the key on replay, and `IContentLinkCommands.IssueNewLinkKeyAsync` is a contract only (no implementation in
  `src/Modules/Links/Application`). So a retried share of a keyed link today gets a link with no usable key.
  **OPEN (user, ARC-002 Q1):** what a retried share returns: (a) the stored link plus a freshly rotated key (the first
  key stops working), (b) the original key kept encrypted for 24 hours (needs an addendum §5.1.2/§16.7 change), or
  (c) the link without a key, as built. No endpoint calls the issuer yet; the rule must be decided before the Stage 2
  snippet and upload-complete endpoints, and the backend then follows it.
- The issuer hashes the password (BCrypt) and mints the link key itself (change F-2): Links cannot reference Access
  (Access references Links.Contracts), so it does not use `ILinkKeys`. Format and hashing: C4 notes, "Link key
  format", which both sides MUST follow. Only the hash is stored on the access policy. `ShortUrl` is built from
  configuration, never from `Host`.
- `ExpiresAt` is clamped by the caller with `ContentLimits.RetentionDays`; the issuer rejects a missing expiry.
- `RequireLinkKey` defaults to `true` for every kind, media included (PD-37, ADR-0014). Callers pass `false` only on
  an explicit owner choice: snippets and files (PD-01), images (PD-37), video and screen recordings (PD-45).
  Switching public/keyed later is a Links operation (remove the key hash / issue a new key) not yet in this stub;
  it is added with BE-007.
- Custom alias (`CustomSlug`): allowed for every caller of a content link, guests included (PD-57), independent of
  the URL-link flag `PlanLimits.CustomSlugs`. Phase 0 alias syntax, denylist and reservation rules apply unchanged;
  guest aliases follow the spec §7.12 guest rules (CAPTCHA, guest counters) and are released at expiry (guest
  `purge_after = expires_at`).
- Content links never carry a fallback URL (addendum §2.1.2, §17). Closed-link answers (PD-39, PD-42, ADR-0016) are
  Resolution behaviour, not part of this contract.

## C2 · Resource envelope

Owner: Resources. Consumers: Snippets, Files, Media (register, mark ready/rejected, kind handlers), Api (descriptor
composition). Decisions: PD-04, PD-05, PD-06, PD-14. Addendum §2.4, §8, §9, §10.

```csharp
namespace Zaagl.Modules.Resources.Contracts;

public enum ResourceKind : short { Snippet = 1, File = 2, Media = 3 }
public enum ProcessingStatus : short { Pending, Scanning, Ready, Rejected, Error }
public enum ResourceState { Purgeable, Deleted, Disabled, Burned, Expired, Processing, Rejected, Ready }
public sealed record ContentOwner(Guid? UserId, byte[]? GuestCreatorKey);

public interface IResourceRegistry
{
    Task<Result<ResourceRegistration>> RegisterAsync(RegisterResourceCommand command, CancellationToken ct);
    Task<Result<bool>> BindLinkAsync(Guid resourceId, Guid linkId, CancellationToken ct);
    Task<Result<bool>> MarkScanningAsync(Guid resourceId, CancellationToken ct);
    Task<Result<bool>> MarkReadyAsync(Guid resourceId, long finalSizeBytes, CancellationToken ct);
    Task<Result<bool>> MarkRejectedAsync(Guid resourceId, string reason, CancellationToken ct);
    Task<ResourceDescriptor?> GetAsync(Guid resourceId, CancellationToken ct);
    Task<ResourceDescriptor?> GetByLinkAsync(Guid linkId, CancellationToken ct);
}

public interface IContentQuota
{
    Task<Result<QuotaReservation>> ReserveAsync(ContentOwner owner, long bytes, CancellationToken ct);
    Task<Result<bool>> CommitAsync(Guid reservationId, long actualBytes, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, CancellationToken ct);
    Task ReleaseUsedAsync(Guid resourceId, CancellationToken ct);
    Task<StorageUsage> GetUsageAsync(Guid userId, CancellationToken ct);
}

public interface IResourceKindHandler
{
    ResourceKind Kind { get; }
    Task OnResourceDeletedAsync(Guid resourceId, CancellationToken ct);
}

public enum DeliveryPurpose : short { Download = 1, Inline = 2, Preview = 3, Raw = 4, Owner = 5 }
public sealed record MintTicketRequest(Guid ResourceId, DeliveryPurpose Purpose, string? Derivative);
public sealed record DeliveryTicket(string Url, DateTime ExpiresAt);
public sealed record DeliveryTicketClaims(string KeyId, Guid GrantId, Guid ResourceId, DeliveryPurpose Purpose, string? Derivative, DateTime ExpiresAt);
public enum DeliveryOutcome { Allowed, TicketInvalid, Burned, Gone, Processing, Rejected }

public interface IDownloadGrants
{
    Task<Result<DeliveryTicket>> MintAsync(MintTicketRequest request, CancellationToken ct);
    Task<DeliveryDecision> AuthorizeAsync(string ticket, bool isHead, CancellationToken ct);
}
```

Notes
- `ResourceState` is computed (never stored), first match wins, same order as addendum §2.4. Only `Ready` serves bytes.
- `PublicId`: 16 CSPRNG bytes, base64url; used in tickets, never in share URLs, never derived from the code.
- Quota ledger changes only under `pg_advisory_xact_lock(hashtextextended('zaagl.quota:'||owner, 0))`.
- Download grants and the burn claim live in the `resources` schema so claim + grant are one transaction (PD-05).
  Column names (single source: ARC-006 §1): `resources.burn_claimed_at`, `resources.burn_grant_id`,
  `download_grants.request_count`. The claim rule (link not expired, deleted or disabled at claim time, addendum
  §8.2.2) is stated once, in ARC-006 §5.
  `Burn:GraceMinutes = 30`, `Burn:MaxRequests = 16`. Download tickets expire 120 s after mint.
- The viewer descriptor is composed in the Api layer from `ResourceDescriptor` plus the kind module's view DTO
  (`SnippetViewDto`, `MediaDto`, file fields), because Resources must not reference the kind modules.
- Resources implements `ILinkLifecycleHandler` (C10).

## C3 · Hosts and URL scheme

Owner: Architect + DevOps. Decisions: PD-02, PD-03, PD-12, PD-38, PD-39, PD-42 to PD-44, PD-46, PD-47 (ADR-0002,
ADR-0003, ADR-0008, ADR-0015, ADR-0016, ADR-0019, ADR-0020, ADR-0022). Addendum §4, §17.

| URL | Meaning |
|---|---|
| `https://zaa.gl/<code>#k=<key>` | Share URL. `#k=` only for keyed links. |
| `302 https://app.zaa.gl/{p,i,f}/<code>` | Redirect target for content kinds (fragment kept by the browser). |
| `https://api.zaa.gl/api/...` | Content API and tus (`/api/uploads`). |
| `https://usercontent.zaa.gl/d/<ticket>` | Every byte: downloads, inline originals, previews, raw snippet text. |
| `zaa.gl/{s,i,f,p}/...` | Reserved, `404` in Phase 1. |

Ticket format: `base64url(payload) "." base64url(HMAC-SHA256(key[kid], payload))`, at most 256 characters (access
grants too), payload = `kid`, grant id, resource
id, purpose, derivative, expiry (Unix seconds). Keys: `Content:TicketKeys` (rotating set, newest signs, all verify).
Nginx: `/d/` proxies to the API authorize hop, which answers `X-Accel-Redirect: /_protected/<storage key>`; the internal
location is `internal;` and adds the addendum §4.4 headers. `usercontent.zaa.gl` is **proxied by Cloudflare** (PD-38):
every response body ≤ `Content:MaxResponseBytes` (to confirm, proposed 90,000,000), larger objects served as `Range`
parts (`206`); a request for an object above the cap without one acceptable byte range gets `416 range-required`
(never a surprise partial body). `410` on `/d/` carries code `burned` or `gone` only; the descriptor and mint routes
give the specific `expired`/`deleted`/`disabled`/`taken-down` code. Cloudflare cache bypassed for the host. Browser assembly of a whole file above one part: OPEN (ARC-008,
FE). No cookie on `usercontent.zaa.gl` or with `Domain=zaa.gl`; verify edge cookies before launch (OPS-001/OPS-010).
Pre-launch: confirm Cloudflare's terms for large non-web files (addendum §20).

Closed short links (PD-39, PD-42): `zaa.gl` answers with its own static `404`/`410` pages (spec §7.3); an expired URL
link with a fallback URL gets `302` to it; a URL link whose click limit is used up gets the `410` "reached its click
limit" page, never the fallback (PD-43); `HEAD` on any click-limited link answers that same `410` status without a
body or `Location` and consumes nothing (PD-47); fallback URLs pass the destination blocklist and Web Risk checks (PD-44);
nothing is redirected to `app.zaa.gl` for a closed link. Addendum §17.

`api.zaa.gl` cookies (PD-46): only the Google/GitHub sign-in routes, only the OAuth handshake cookies, host-only,
short-lived. Addendum §4.1.2.

## C4 · Access gate, link key, grants

Owner: Access. Consumers: Resolution, Resources, Files, FE, CLI, Desktop. Decisions: PD-01, PD-37 (ADR-0001,
ADR-0014). Addendum §5.

```csharp
namespace Zaagl.Modules.Access.Contracts;

public static class AccessWireNames
{
    public const string LinkKeyFragmentParameter = "k";
    public const string LinkKeyHeader = "X-Zaagl-Link-Key";
    public const string GrantHeader = "X-Zaagl-Grant";
    public const int LinkKeyBytes = 16;
    public const int LinkKeyEncodedLength = 22;
}

public enum AccessPurpose { Redirect, View, Raw, Preview, Download }
public enum AccessGateOutcome { Allowed, PasswordRequired, NotFound, Gone, Disabled, TakenDown }
public sealed record AccessCredentials(string? LinkKey, string? GrantToken);
public sealed record AccessGateResult(AccessGateOutcome Outcome, Guid? LinkId, Guid? ResourceId, LinkTargetKind? Kind, bool IsGuest,
    LinkState? LinkState = null);

public interface IAccessGate
{
    Task<AccessGateResult> EvaluateAsync(string code, AccessCredentials credentials, AccessPurpose purpose, CancellationToken ct);
}

public sealed record AccessGrantClaims(string KeyId, Guid LinkId, Guid? ResourceId, IReadOnlyList<AccessPurpose> Purposes, DateTime IssuedAt, DateTime ExpiresAt,
    int PasswordStamp);
public sealed record AccessGrant(string Token, DateTime ExpiresAt);

public interface IAccessGrants
{
    Task<Result<AccessGrant>> UnlockAsync(string code, string password, string? linkKey, CancellationToken ct);
    Result<AccessGrantClaims> Validate(string token, Guid linkId, AccessPurpose purpose);
}

public sealed record NewLinkKey(string PlainKey, byte[] Sha256Hash);
public interface ILinkKeys { NewLinkKey Generate(); bool Matches(string? presentedKey, byte[] storedSha256Hash); }
```

Notes
- Gate order: state → link key (wrong/missing → `NotFound`, indistinguishable from no code) → password/grant. Never
  consumes anything.
- Purpose `Redirect` checks state only (the redirect never sees the fragment).
- Grant lifetime default 10 minutes, key set `Access:GrantKeys` rotating like the JWT signing keys. Wire format,
  validation and threat model: `design/ARC-004-access-gate.md`. Grant tokens are at most 256 characters. Stub fields: `AccessGateResult.LinkState`
  (so Resources can rank `burned` above `expired`, addendum §2.4) and `AccessGrantClaims.PasswordStamp` (a password
  change revokes outstanding grants).
- Existing `Access.Application.AccessGate`/`AccessOutcome` stay for the Phase 0 redirect until the backend moves
  Resolution onto `IAccessGate`; QA-014 then forbids the concrete reference (`ResolutionService.cs:5,7`).
- Unlock on a link that has no password → `400 validation`.
- Link key format (change F-2; binding for the Links issuer and for Access's verifier, so Stage 2 unlocks match what
  Stage 1 stored). Minting, as built in `src/Modules/Links/Application/LinkService.Content.cs:69-77`:
  - raw key: 16 bytes from `RandomNumberGenerator.GetBytes(16)` (`:74`);
  - plain key: `System.Buffers.Text.Base64Url.EncodeToString(raw)`: RFC 4648 §5 alphabet (`A-Z a-z 0-9 - _`), no
    padding, exactly 22 characters (`:75`); returned once in `ContentLinkIssued.LinkKey`, sent by clients as `#k=` and
    the `X-Zaagl-Link-Key` header;
  - stored hash: `SHA256.HashData(raw)`, plain SHA-256 of the **16 raw bytes**, 32 bytes, no pepper, no salt, no HMAC
    (`:76`); stored in `links.access_policies.link_key_hash` (`bytea`, `src/Modules/Links/Domain/Link.cs:119-120`) and
    carried in `LinkSnapshot.LinkKeyHash`.
  - Verification (Access, `ILinkKeys.Matches` / the gate): the presented value must be exactly 22 characters of that
    alphabet and decode with `Base64Url` to exactly 16 bytes, otherwise it is a wrong key; then compare
    `SHA256(decoded 16 bytes)` with the stored hash using `CryptographicOperations.FixedTimeEquals`. Never hash the
    22-character string or its UTF-8 bytes: that never matches and every keyed link would fail to unlock.
  - `ILinkKeys.Generate` stays in the stub but has no caller in Stage 1; any future minting (including
    `IContentLinkCommands.IssueNewLinkKeyAsync` in Links) MUST produce this exact format. Moving key minting into a
    shared helper (Links.Contracts or SharedKernel) is allowed later as a refactor without a format change.
- Max views / max downloads for content: not in this contract until PD-10 is decided.

## C5 · Content limits and capability flags

Owner: Billing. Consumers: every backend module that applies a plan rule. Decisions: PD-04, PD-07, PD-14 (ADR-0004).
Addendum §10, §11.

```csharp
namespace Zaagl.Modules.Billing.Contracts;

public sealed record PlanCapabilities(bool IsPaid, bool ApiKeys, bool KeepStatsAfterPurge,
    bool BurnAfterDownload, bool PasswordOnContent, bool ContentStatistics, bool ManageContent);
public sealed record ContentLimits(long MaxFileBytes, long MaxSnippetBytes, int RetentionDays,
    bool QuarantineAfterExpiry, long? StorageQuotaBytes);
public sealed record ContentPlan(PlanTier? Tier, bool IsGuest, ContentLimits Limits, PlanCapabilities Capabilities);

public interface IContentPlanProvider
{
    Task<ContentPlan> GetForUserAsync(Guid userId, CancellationToken ct);
    ContentPlan Guest { get; }
}
```

| Configuration (`Plans:<plan>:Content:*`) | Guest | Free | Starter | Status |
|---|---|---|---|---|
| `MaxFileBytes` | 25 MB (26,214,400) | 25 MB (26,214,400) | 100 MB (104,857,600) | approved (PD-04, PD-54) |
| `RetentionDays` | 7 | 7 | 30 | approved |
| `QuarantineAfterExpiry` | false | true | true | approved (PD-06) |
| `StorageQuotaBytes` | null | 250 MB (262,144,000) | 5 GB (5,368,709,120) | approved (PD-54) |
| `MaxSnippetBytes` | 256 KB (262,144) | 512 KB (524,288) | 2 MB (2,097,152) | approved (PD-56) |
| `ApiKeys` | false | false | true | approved (PD-07) |
| `IsPaid` / `KeepStatsAfterPurge` | false | false | true | Phase 0 behaviour |
| `BurnAfterDownload`, `PasswordOnContent` | false | false | true | approved (PD-54: Starter only) |
| `ContentStatistics`, `ManageContent` | false | true | true | approved (spec §7.12) |

Byte unit (PD-54): 1 MB = 1,048,576 bytes; likewise 1 KB = 1,024 and 1 GB = 1,073,741,824 bytes. Every value is
configuration.

Notes
- No `PlanTier.Pro` (PD-04). `PlanLimits` keeps its Phase 0 behaviour; content code reads only `ContentPlan`.
- `ContentPlan.Tier` is exposed for Billing and display only; the "no tier comparison outside Billing" rule (addendum
  §10.2) MUST cover it (NetArchTest/analyzer rule, Head review).
- BE-003 replaces the tier checks outside Billing, `PurgeService.cs:102` and `IdentityService.cs:442` (moved from
  `:395`), with flags; `BillingService.cs:58,149,164` are inside Billing and may keep the tier. A grep test forbids
  `PlanTier.` equality outside Billing. `BillingStatusDto` gains `Capabilities` (stub, this round) so the frontend stops
  comparing `tier === 1`. Gap: API keys keep working after a downgrade (`IdentityService.cs:603-614`); BE-003 adds the
  `ApiKeys` check. Every value in the table is approved; a value added later ships fail-closed until approved.
  Details: `design/ARC-005-plan-model-v2.md`.
- Post-freeze addition (change F-1):

  ```csharp
  public interface IPlanChangeListener
  {
      Task OnPlanChangedAsync(Guid userId, PlanCapabilities previous, PlanCapabilities current, CancellationToken cancellationToken);
  }
  ```

  Rationale: spec §7.4 requires that a downgrade which removes `ApiKeys` revokes the user's keys at once and sends a
  security notice. API keys live in Identity, plan changes happen in Billing (Stripe webhook), and Billing must not
  reference Identity; a listener interface owned by Billing.Contracts lets Identity react without a reverse reference.
  Built: Billing calls every registered listener from `AfterPlanChangeAsync` when the tier changed
  (`src/Modules/Billing/Application/BillingService.cs:214-235`); Identity registers `ApiKeyPlanChangeListener`
  (`src/Modules/Identity/IdentityModule.cs:33`), which revokes all active keys and queues the notice when
  `previous.ApiKeys && !current.ApiKeys` (`src/Modules/Identity/Application/ApiKeyPlanChangeListener.cs:8-11`,
  `IdentityService.cs:473-490`). An upgrade never restores revoked keys (spec §7.4; ARC-005 §4).
  Deviation from spec §8.2: the listener runs **after** Billing's commit, not in the same transaction (separate module
  schemas, no cross-module outbox yet); a listener failure is logged and not retried (`BillingService.cs:226-233`). The
  backstop is the plan check in key authentication (`IdentityService.cs:629-632`), so a key of a plan without `ApiKeys`
  fails with `401` within the 60-second plan cache even if revocation did not run. Whether to accept this until an
  outbox exists is Q1 of `reviews/stage1-final.md` (OPEN, user). Head note B2 (revoke whenever `!current.ApiKeys`,
  not only on a `true → false` edge) is a backend follow-up, not a contract change.

## C6 · Analytics event kind

Owner: Analytics. Consumers: Resolution, Resources, Files, FE. Addendum §12. Stubbed 2026-11-09.

```csharp
namespace Zaagl.Modules.Analytics.Contracts;
public enum ClickEventKind : short { Redirect = 0, View = 1, Raw = 2, Download = 3 }
public sealed record ClickEvent(..., UtmTags? Utm = null, ClickEventKind Kind = ClickEventKind.Redirect);
```

Notes: `kind smallint not null default 0` on `click_events` (per-partition indexes) and `link_daily_stats`. Guest content
records no events. One source of truth for downloads: the durable grant table (burn, billing and the owner's
`downloadCount`), never this pipeline. Content events (`View`, `Raw`, `Download`) are not written until PD-10
(PROPOSED) is approved; the headline "views" metric stays undefined until then.

## C7 · Object storage v2

Owner: Files. Consumers: Media, the authorize hop, DevOps (layout). Addendum §7, §8.

```csharp
namespace Zaagl.Modules.Files.Contracts;

public enum StorageArea : short { Temp, Objects, Quarantine, Previews, Thumbnails }
public enum ScanVerdict : short { Pending, Clean, Infected, Error }
public sealed record StoredObjectInfo(Guid ObjectId, string StorageKey, StorageArea Area, long SizeBytes, byte[] Sha256, string DetectedMime, ScanVerdict ScanVerdict);
public sealed record PutResult(string StorageKey, long SizeBytes, byte[] Sha256);

public interface IObjectStorage
{
    Task<PutResult> PutAsync(StorageArea area, Stream content, CancellationToken ct);
    Task<string> MoveAsync(string storageKey, StorageArea target, CancellationToken ct);
    Task<Stream> OpenReadAsync(string storageKey, long offset, long? length, CancellationToken ct);
    Task<long?> StatAsync(string storageKey, CancellationToken ct);
    Task DeleteAsync(string storageKey, CancellationToken ct);
    string GetInternalPath(string storageKey);
}

public interface IStoredObjects
{
    Task<StoredObjectInfo?> GetForResourceAsync(Guid resourceId, CancellationToken ct);
    Task QueueDeletionAsync(Guid resourceId, string reason, CancellationToken ct);
    Task QuarantineAsync(Guid resourceId, DateTime legalHoldUntil, CancellationToken ct);   // legal duties PD-11 OPEN
}
```

Notes: replaces the unused `SharedKernel.Storage.IFileStorage` (zero consumers; BE-004 removes it). Keys are generated
and validated (no traversal); shard from a hash of the id; one filesystem so moves are atomic renames. PD-15 APPROVED: the
server's own disk behind this interface (built in BE-004); S3/R2 can be added later as another implementation. Too-large-to-scan does not occur in Phase 1 (100 MB max), so
`ScanVerdict` has no "skipped" value.

## C8 · Upload protocol

Owner: Files. Consumers: FE uploader, CLI, Desktop, QA. Decision: PD-12 (ADR-0008). Addendum §6.

```csharp
public static class UploadProtocol
{
    public const string TusVersion = "1.0.0";
    public const long MaxChunkBytes = 90_000_000;
    public const long DefaultChunkBytes = 64L * 1024 * 1024;
    public const int SessionIdleHours = 24;
    public const string MetadataFileName = "filename", MetadataFileType = "filetype", MetadataKind = "kind";
}
public enum UploadSessionStatus : short { Open, Complete, Finalized, Aborted, Expired }
public sealed record CompleteUploadRequest(string? Title, DateTime? ExpiresAt, bool BurnAfterDownload, string? Password, string? CustomSlug, bool Public);
public sealed record CompleteUploadResponse(Guid ResourceId, string Code, string ShortUrl, string? LinkKey, string State, DateTime ExpiresAt);
```

PD-28 APPROVED: the link is returned at completion and may be shared at once; the viewer shows "processing" and no
byte is served until the scan is clean (`ready`). `CustomSlug`: allowed for guests too (PD-57). `BurnAfterDownload`
and `Password`: Starter only (PD-54), otherwise `402 plan_required`.

Notes: tus on `api.zaa.gl/api/uploads` (OPTIONS, POST create, HEAD, PATCH, DELETE) + `POST /api/uploads/{id}/complete`
(Idempotency-Key required). Extensions: creation, termination, expiration, checksum. `Upload-Length` mandatory; plan
size and quota checked at create before any byte. Kestrel's global body limit stays small; only tus PATCH and
`/uploads/simple` raise it to `MaxChunkBytes`. Session ownership = capability id + principal match. Guests: CAPTCHA at
create and the PD-14 counters (C9). The frontend draft's `uploadToken` on a separate files host (API-U1/U2) is not
taken: the BFF or native client calls `api.zaa.gl` with its normal credentials.

## C9 · Public content REST API (input to ARC-016 / BE-028 OpenAPI)

Owner: architect review, module owners implement. Prefix OPEN (PD-33). Auth: J = JWT/device session, K(scope) = API
key, G = guest (`Captcha-Token`), P = public (gate evaluated, `X-Zaagl-Link-Key`, `X-Zaagl-Grant`).

| Method and route | Auth | Result |
|---|---|---|
| `POST /api/snippets` | J, K(snippets:write), G | `201 SnippetCreatedDto` (with `linkKey` once) |
| `POST /api/snippets/raw` (CLI pipe, streamed) | J, K(snippets:write) | `201`; `Accept: text/plain` → URL incl. `#k=` + `\n` |
| `GET/PUT/DELETE /api/snippets/{id}`, versions, diff | owner | versions immutable; `409 version-conflict` |
| `POST /api/uploads` … `/complete` (C8) | J, K(files:write), G | `202 CompleteUploadResponse` |
| `POST /api/uploads/simple` (one request, body ≤ 90,000,000 bytes) | J, K(files:write), G | `202 CompleteUploadResponse` |
| `GET /api/resources`, `/api/resources/{id}`, `/status` | J, K(resources:read) | owner list/detail/processing status |
| `PATCH/DELETE /api/resources/{id}` | owner (not guests); key needs `snippets:write` (snippets) or `files:write` (files, media) | `400 expiry-beyond-plan`, `409 burn-already-claimed` |
| `POST /api/resources/{id}/download` | owner; key needs `resources:read` | owner ticket (never burns) |
| `GET /api/public/content/{code}` | P | descriptor; never a download ticket; `Cache-Control: private, no-store` |
| `POST /api/public/content/{code}/unlock` | P, `unlock` limit | `{grant, expiresAt}` |
| `POST /api/public/content/{code}/download` | P | `{url: usercontent.zaa.gl/d/<ticket>, expiresAt}` (120 s) |
| `GET/HEAD usercontent.zaa.gl/d/{ticket}` | ticket | bytes via X-Accel; addendum §4.4 headers |
| `GET /api/me/storage` | J, K(resources:read) | limits, used, reserved, quota |
| `POST /api/reports` | anyone + CAPTCHA | spec §7.15 |

Errors (RFC 7807 `type` from BE-002). This catalogue is the single list; ARC-004/006/008 and
`design/openapi-phase1.yaml` use exactly these codes. "In `ErrorCodes`" = present in `src/Zaagl.SharedKernel/Result.cs`
on 2026-11-09 (BE-002); codes marked **no** are added by the backend with the route that returns them.

| Status | Codes | In `ErrorCodes` |
|---|---|---|
| 400 | `validation`, `expiry-beyond-plan`, `upload-length-required` | yes |
| 401 | `unauthorized`, `password-required`, `captcha-required`, `captcha-invalid` | yes |
| 402 | `plan_required` | yes |
| 403 | `insufficient-scope`, `quota-exceeded`, `ticket-invalid` | yes |
| 404 | `not_found` | yes |
| 409 | `conflict`, `upload-offset-mismatch`, `upload-incomplete`, `already-finalized`, `processing`, `burn-already-claimed`, `content-link` | yes |
| 409 | `version-conflict` | **no** |
| 410 | `expired`, `burned`, `upload-expired` | yes |
| 410 | `deleted`, `disabled`, `taken-down` (API routes, ARC-004 §3); `gone` (`usercontent.zaa.gl/d/` only, ARC-008 §4.5) | **no** |
| 411 | `length-required` | **no** |
| 413 | `file-too-large`, `chunk-too-large`, `snippet-too-large` | yes |
| 413 | `diff-too-large` | **no** |
| 415 | `unsupported-media-type` | yes |
| 416 | `range-required` (`usercontent.zaa.gl/d/` only) | **no** |
| 422 | `idempotency-mismatch`, `content-rejected` | yes |
| 429 | `quota` (guest counters), `too_many_requests`, `rate-limit` | yes |
| 460 | `checksum-mismatch` (tus checksum extension) | yes |
| 503 | `service_unavailable`, `guest_creation_paused` | yes |
| 503 | `busy` (lock timeout, `Retry-After: 1`) | **no** |
| 507 | `insufficient-storage` (disk admission, `Retry-After`) | **no** |

`content-link` (a URL-link route asked to act on a content link) and `already-finalized` (tus complete on a finalized
session) come from BE-002 and are now part of the catalogue.

Guest limits (PD-14 as updated 2026-11-09): 30 per hour and 60 per rolling 24 h per IP partition (IPv4 address, IPv6
/64), snippets and uploads counted together, hard limits in locked hourly buckets; /56 and /48 proportional (to
confirm); enforceable only once BE-030 is deployed.

Credentials on guest-capable routes (PD-36, ADR-0013, addendum §19): a route marked G is a guest only when the request
carries **no** credential. Any credential that fails (wrong, revoked, expired, superseded, key of a plan without
`ApiKeys`, enrol-only token) → `401` with `WWW-Authenticate`, before CAPTCHA, guest counters or any reservation.
This binds **every** Phase 1 guest endpoint, not only `POST /api/links`: `POST /api/snippets` (snippet create),
`POST /api/uploads` (upload-session create) and `POST /api/uploads/simple` MUST reject a failed credential with `401`
exactly like PD-36, and never fall through to the guest path (Head note). Each route × failing-credential variant has a
test asserting `401` and that no guest counter moved.

Owner routes (PD-40, ADR-0017, addendum §18): "owner" in this table means the owner only. Any other principal, admin
included, gets `404`. Admin access to other users' links and content is only through `/api/admin/*`.

## C10 · Link lifecycle events

Owner: Links (outbox, emitter) + Resources (handler). Consumers: Files, Media (through Resources). Addendum §2.3, §9.

```csharp
public abstract record LinkLifecycleEvent(long OutboxId, Guid LinkId, Guid ResourceId, LinkTargetKind Kind, DateTime OccurredAt);
public sealed record LinkDeleted(...)  : LinkLifecycleEvent(...);
public sealed record LinkDisabled(..., string DisabledKind) : LinkLifecycleEvent(...);
public sealed record LinkEnabled(...)  : LinkLifecycleEvent(...);
public sealed record LinkRenewed(..., DateTime ExpiresAt, DateTime PurgeAfter) : LinkLifecycleEvent(...);
public sealed record LinkExpired(..., bool IsGuest) : LinkLifecycleEvent(...);
public sealed record LinkPurged(...)   : LinkLifecycleEvent(...);

public interface ILinkLifecycleHandler { Task HandleAsync(LinkLifecycleEvent e, CancellationToken ct); }
```

Notes: `links.lifecycle_outbox(id bigint identity, link_id, resource_id, kind, event smallint, payload jsonb,
occurred_at, processed_at null, attempts, last_error)`, rows written in the same transaction as the change, only for
content kinds. `LinkExpired` is emitted by a job (expiry is time-based, not a write). Drained by the worker with `FOR
UPDATE SKIP LOCKED`; at-least-once; handlers idempotent. Reactions: Deleted → bytes unreachable at once, kept until
`purge_after`, storage freed only at purge (PD-55); Disabled abuse/safety → quarantine bytes with legal hold (legal
duties PD-11 OPEN; storage release at takedown OPEN, ARC-006 Q2); Renewed → move byte deletion to new
`purge_after`; Expired (guest) → delete bytes; Purged → delete everything left.

## C11 · Native client authentication and scopes

Owner: Identity. Consumers: CLI, Desktop, FE (`/activate`). Decisions: PD-07, PD-19, PD-24 (ADR-0007, ADR-0010,
ADR-0012). Addendum §14. Stubbed 2026-11-09 in `src/Modules/Identity/Contracts/NativeClientContracts.cs`:
`TokenScopes`, `NativeClientIds`, `NativeClientClaims` (`client_id`, `amr=device`, `sid`), `DeviceFlowWire` (RFC 8628
values and errors), `DeviceCodeRequest/Response`, `DeviceTokenRequest/Error`, `DeviceActivationInfo/Request`,
`DeviceSessionDto`. Flow, storage, scope matrix and gaps: `design/ARC-014-native-client-auth.md`.

- Device authorization (RFC 8628): `POST /api/auth/device/code {client_id: zaagl-cli|zaagl-desktop, device_name, os}`
  → `{device_code, user_code (XXXX-XXXX), verification_uri: https://app.zaa.gl/activate, verification_uri_complete,
  expires_in: 600, interval: 5}`. `verification_uri` comes from configuration
  (`DeviceAuth:VerificationUri`, key in `DeviceFlowWire.VerificationUriConfigKey`; production default
  `https://app.zaa.gl/activate`), so staging uses its own app host. `device_name` ≤ 64 characters, `os` ≤ 32.
  Rate-limit policies `device-code`, `device-activate`, `device-token` (ARC-014 §2); `POST /api/auth/device/token {grant_type: urn:ietf:params:oauth:grant-type:device_code,
  device_code}` → existing `AuthResponse` + `sessionId`. Every plan.
- Native tokens: claims `client_id`, `amr=device`; never Admin; excluded from the Interactive policy.
- Scopes (BE-001): `links:read`, `links:write`, `stats:read`, `snippets:write`, `files:write`, `resources:read`.
  The desktop draft's single `content:write` is not taken. API keys: paid only (`ApiKeys` capability).
- Desktop shortcut default Ctrl+Alt+Z / Cmd+Ctrl+Z (PD-19).
- Browser sign-in (PD-41, ADR-0018, addendum §4.5), required before Stage 2 content goes live:
  - Social sign-in: the provider callback redirects to the BFF with a **one-time sign-in code** (never a token in the
    URL). The BFF redeems it server-side (internal network) for the existing `AuthResponse`. Code: single-use, stored
    hashed, lifetime to confirm (proposed 60 s), bound to a verifier the BFF holds from the start of the flow. Route
    names: BE with ARC-016 (not yet in this contract).
  - Live statistics: the BFF obtains a **hub ticket** server-side and gives only that to page script. Valid only to
    connect to the statistics hub, bound to one user, single-use at connect, lifetime to confirm (proposed 60 s). The
    hub stops accepting `access_token` in the query string (`Program.cs:118-123`) once the ticket ships.
  - Implemented by the backend in Stage 1 (Identity, not part of these stubs): `POST /api/auth/external/exchange` and
    `POST /api/auth/hub-ticket` (`src/Zaagl.Api/Endpoints/IdentityEndpoints.cs:171,183`).
- Proposed, not approved: device session list/revoke at `GET/DELETE /api/me/sessions*` (Interactive policy).
- Owner content management (`PATCH/DELETE /api/resources/{id}`) needs the kind's write scope (`snippets:write` or
  `files:write`); owner download and `GET /api/me/storage` need `resources:read`. A separate `resources:write` scope is
  OPEN (ARC-014 Q3) and is not in `TokenScopes`.

## C12 · Content abuse

Owner: Links (takedown, evidence) + Files (hash blocklist, quarantine). Consumers: Admin UI, QA. Addendum §13.

```csharp
// Links.Contracts
public enum ContentTakedownKind { Abuse, Safety }
public sealed record ContentTakedownRequest(Guid LinkId, Guid ResourceId, ContentTakedownKind Kind, string Reason, byte[]? Sha256);
public interface ILinkTakedown { Task<Result<bool>> TakeDownContentAsync(ContentTakedownRequest request, CancellationToken ct); }

// Files.Contracts
public sealed record BlockedContentHash(byte[] Sha256, string Reason, DateTime CreatedAt);
public interface IContentHashBlocklist
{
    Task<bool> IsBlockedAsync(byte[] sha256, CancellationToken ct);
    Task<Result<int>> AddAsync(byte[] sha256, string reason, CancellationToken ct);
}
```

Notes: `abuse_evidence` gains `resource_id`, `sha256`, `object_key`; takedown moves bytes to quarantine with
`legal_hold_until` (column of `resources.resources`, ARC-006 §1); safety re-check filters `target_kind = 0`; bulk takedown filters gain `sha256`, `target_kind`. Legal
duties: OPEN (PD-11).

## Snippet and media view types

`src/Modules/Snippets/Contracts/SnippetContracts.cs`: `SnippetRenderMode`, `CreateSnippetCommand`, `SnippetCreatedDto`,
`SnippetVersionDto`, `SnippetViewDto`, `ISnippetReader`. Markdown renderer location OPEN (PD-09); size caps approved (PD-56); version limit and
diff scope PROPOSED (PD-17).

`src/Modules/Media/Contracts/MediaContracts.cs`: `MediaType`, `DerivativeKinds`, `DerivativeStatus`,
`MediaDerivativeDto`, `MediaDto`, `MediaProbeResult`, `IMediaProbe`, `IMediaReader`. EXIF policy (PD-13), allowlist
(PD-16), SVG (PD-18), transcoding (PD-30) are not decided by these types.

## Open items that block a contract detail

| Item | Contract | Decision |
|---|---|---|
| Guest public choice at creation | C1, C4 | to confirm (owner choice settled by PD-01, PD-37, PD-45) |
| Browser assembly of whole-file downloads above one response part; `Content:MaxResponseBytes` value | C3 | OPEN (ARC-008, FE) / to confirm (PD-38) |
| Sign-in code and hub ticket routes and lifetimes | C11 | PD-41 approved; details BE + ARC-016 (sign-in cookies settled by PD-46) |
| Max views/downloads and the "view" definition | C4, C6 | PD-10 (PROPOSED, not approved) |
| /56 and /48 guest caps; do guest URL links share the content guest budget? | C9 | to confirm / OPEN |
| Storage release at takedown (at takedown, or when the legal hold ends) | C10, C12 | OPEN (ARC-006 Q2, PD-55 note) |
| API prefix `/api` vs `/api/v1` | C9 | PD-33 |
| What a retried share returns for a keyed link: rotated key, 24 h encrypted copy, or no key (built today: no key) | C1 | **OPEN** (user, ARC-002 Q1; change F-3) |
| API-key revocation after Billing's commit instead of in the same transaction (spec §8.2) | C5 | **OPEN** (user, `reviews/stage1-final.md` Q1; change F-1) |
| Content in the main link library; PD-08 monthly cap | C1 | to confirm / PD-08 PROPOSED |
| `resources:write` scope; device session routes | C9, C11 | OPEN (ARC-014 Q3, Q4) |
