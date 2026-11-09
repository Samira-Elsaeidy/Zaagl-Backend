# ARC-002 · Link target model (content links)

| | |
|---|---|
| Owner | zaagl-architect. Implementer: backend (BE-007). |
| Status | DESIGN, ready to implement. Contract C1 frozen (contracts.md). |
| Rules | Addendum §2.1, §2.2, §3, §16 items 9-10. Decisions PD-01, PD-02, PD-37, PD-45. ADR-0001, ADR-0002. |
| Line numbers | Taken from the working tree on 2026-11-09. `LinkService.cs` is being edited by another agent and shifts by a few lines; every site is also anchored by its method name. |

Goal: a content link is a normal `links.links` row whose target is a resource. URL links (`target_kind = 0`) behave exactly
as in Phase 0 and every Phase 0 test passes unchanged. The Phase 0 link engine (leased ranges, keyed scramble codes,
states, gates) is reused, not redesigned.

## 1. Schema (expand-only, spec MUST NOT #32)

Two migrations in the Links DbContext. Migration A runs inside EF's normal transaction; migration B contains only the
concurrent index and uses `suppressTransaction: true`, because `CREATE INDEX CONCURRENTLY` cannot run in a
transaction.

Migration A `AddLinkTargetKind`:

```sql
ALTER TABLE links.links ADD COLUMN target_kind smallint NOT NULL DEFAULT 0;   -- PG 11+: no table rewrite
ALTER TABLE links.links ADD COLUMN resource_id uuid NULL;
ALTER TABLE links.links ALTER COLUMN destination_url DROP NOT NULL;            -- catalog-only change
ALTER TABLE links.links ALTER COLUMN target_host DROP NOT NULL;
ALTER TABLE links.access_policies ADD COLUMN link_key_hash bytea NULL;          -- C4, SHA-256 (32 bytes)

ALTER TABLE links.links ADD CONSTRAINT ck_links_target_kind
  CHECK (target_kind IN (0, 1, 2, 3)) NOT VALID;
ALTER TABLE links.links ADD CONSTRAINT ck_links_target_resource
  CHECK ((target_kind = 0) = (resource_id IS NULL)) NOT VALID;
ALTER TABLE links.links ADD CONSTRAINT ck_links_target_destination
  CHECK ((target_kind = 0) = (destination_url IS NOT NULL AND target_host IS NOT NULL)) NOT VALID;
ALTER TABLE links.access_policies ADD CONSTRAINT ck_access_policies_link_key_hash
  CHECK (link_key_hash IS NULL OR octet_length(link_key_hash) = 32) NOT VALID;

ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_kind;              -- SHARE UPDATE EXCLUSIVE only
ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_resource;
ALTER TABLE links.links VALIDATE CONSTRAINT ck_links_target_destination;
ALTER TABLE links.access_policies VALIDATE CONSTRAINT ck_access_policies_link_key_hash;
```

Migration B `AddLinkResourceIndex` (`migrationBuilder.Sql(..., suppressTransaction: true)`):

```sql
CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_links_resource_id
  ON links.links (resource_id) WHERE resource_id IS NOT NULL;   -- exactly one link per resource (addendum §1)
```

If a concurrent build fails it leaves an `INVALID` index; the runbook step is `DROP INDEX CONCURRENTLY` and rerun.
`ck_links_target_destination` is stricter than the addendum text (which only says the columns become nullable "for
content kinds"); it makes the rule enforceable by the database and holds for every Phase 0 row.

EF model (`src/Modules/Links/Domain/Link.cs:43-47`, `src/Modules/Links/Infrastructure/LinksDbContext.cs:35-36,51`):
`Link.DestinationUrl` and `Link.TargetHost` become `string?` with `IsRequired(false)`; add
`LinkTargetKind TargetKind` (`HasConversion<short>()`) and `Guid? ResourceId`; `AccessPolicy.LinkKeyHash byte[]?`;
declare the three check constraints with `HasCheckConstraint` so the model snapshot matches. The existing `TargetHost`
index (`LinksDbContext.cs:51`) stays (null rows are simply not matched).

## 2. Contract changes (C1)

Already stubbed (this round, trailing optional parameters, source-compatible with every caller):
- `LinkSnapshot`: `TargetKind`, `ResourceId`, `LinkKeyHash` (`src/Modules/Links/Contracts/LinkContracts.cs`).
- `LinkDto`: `TargetKind`, `ResourceId`.
- `ILinkIssuer`, `ContentLinkRequest`, `ContentLinkIssued`, new `IContentLinkCommands`, `ContentLinkRenewed`
  (`src/Modules/Links/Contracts/ContentLinkContracts.cs`).

Frozen, applied by BE-007 in the same change as the null-handling branches below (flipping them earlier only adds
nullable warnings in files another agent is editing):
- `LinkSnapshot.DestinationUrl`, `LinkDto.DestinationUrl`, `AdminLinkDto.DestinationUrl` and `AdminLinkDto.TargetHost`
  become `string?`; `AdminLinkDto` gains `TargetKind` and `ResourceId` (trailing).
- Export: `LinkExportRow.TargetUrl` and Identity's `ExportLinkDto.TargetUrl` / `ExportArchivedLinkDto.TargetUrl`
  become `string?`, and each gains a trailing `string Kind = "url"` (`url|snippet|file|media`).
- `ResolveKind.Content` (Resolution, not a contract) with `ResolveResult.Url` = the viewer URL.

## 3. Versioned cache key

- `LinkCache.Key` (`LinkService.cs:28-31`) becomes `$"link:v2:{slugKey}"`. Entries written by an older build under
  `link:{slugKey}` are never read by the new build, so a snapshot without `TargetKind` is never deserialised (addendum
  §2.1.5). Old entries age out (5 min L2, `LinkResolver.cs:11-15`).
- `LinkCacheInvalidationListener` hard-codes the old shape: `cache.RemoveAsync($"link:{key}", ...)`
  (`src/Modules/Links/Infrastructure/LinkCacheInvalidation.cs:98`). It MUST call `LinkCache.Key(key)`; otherwise
  NOTIFY invalidation silently stops working after the bump. A rolling deploy is safe: each instance removes its own
  key shape on the shared NOTIFY payload (the slug key).
- Rule for later shape changes: bump the version segment whenever `LinkSnapshot` gains a field whose default would be
  wrong for existing links.

## 4. Snapshot load

`LinkResolver.LoadAsync` (`src/Modules/Links/Application/LinkResolver.cs:58-125`): add `l.target_kind, l.resource_id,
p.link_key_hash` to the SELECT (`:73-76`), read `destination_url` with `IsDBNull` (`:88` today calls `GetString(3)`,
which throws on null), and pass the three new values to `new LinkSnapshot(...)` (`:122`). The redirect pool and the raw
SQL stay (spec §7.0).

## 5. ILinkIssuer implementation

`ContentLinkIssuer : ILinkIssuer, IContentLinkCommands` in `Links.Application`, registered in `LinksModule`.

1. Validate: `Kind != Url`; exactly one of `OwnerId`, `GuestCreatorKey`; `ExpiresAt > now` (`TimeProvider`); a
   missing or past expiry → `validation`. The caller already clamped expiry to the plan (addendum §9.1); the issuer
   does not read plans.
2. Idempotency: scope `u:{ownerId}` or `g:{hex(creatorKey)}` (same scopes as `CreateAsync`/`CreateGuestAsync`,
   `LinkService.cs:72-86, 267-285`), key `content:{resourceId:N}`, request hash = SHA-256 of
   `kind \n resourceId \n customSlug \n expiresAt(ISO, ms) \n requireLinkKey \n (password != null)`. Same claim/complete
   functions (`TryClaimIdempotencyKeyAsync`, `CompleteIdempotencyKeyAsync`, `LinkService.cs:449-473`).
3. Slug allocation and insert: reuse the Phase 0 loop exactly (leases + scramble, reserved-word skip, restore freeze,
   denylist, `blocked_aliases`, advisory alias lock, unique-violation retry). Recommended mechanics: extract the loop
   body of `CreateAsync` into a private `AllocateAndInsertAsync(LinkDraft, ...)` that both URL creation paths and the
   issuer call. This is a move without behaviour change, checked by the unchanged Phase 0 tests. Not taken: copying
   the loop into the issuer (two copies drift).
4. Steps the issuer MUST NOT run (addendum §16.9): `Normalize`/`UrlSafety` (`LinkService.cs:937-981`), Web Risk in
   `PrepareAsync` (`:888-905`), `BlockedUrlErrorAsync` (`:912-920`), routing rules, fallback URL, Open Graph input,
   `MonthlyLinks` (`:96-103`), `CheckPlan` (`:922-935`; content plan rules are checked by the caller through C5).
5. Row: `TargetKind`, `ResourceId`, `DestinationUrl = null`, `TargetHost = null`, `Title`, `PublicStats = false`,
   `IsActive = true`, policy `{ ExpiresAt, PasswordHash = BCrypt(password) when set, LinkKeyHash when
   RequireLinkKey }`, `MaxClicks = null` (addendum §3.7), `FallbackUrl = null` (§17). `PurgeAfter`: account
   `expires_at + Links:QuarantineDays`; guest `= expires_at` (addendum §9.3).
6. Response: `ContentLinkIssued(LinkId, Code, ShortUrl = ShortLinks:BaseUrl + "/" + code, ExpiresAt, PurgeAfter,
   LinkKey)`. `ShortLinks:BaseUrl` (`appsettings.json:48-50`), never the request `Host`.

Idempotent replay and the link key. The plain key may not be stored anywhere (addendum §5.1.2, §16.7), so a replay
cannot return the original key. **Built (Stage 1):** a replay returns the stored link data with `LinkKey = null` and
nothing else; the issuer does not rotate the key (`src/Modules/Links/Application/LinkService.Content.cs:54-64`,
`:91-95`, `:120-121`), and `IContentLinkCommands.IssueNewLinkKeyAsync` exists only as a contract. A retried share of a
keyed link therefore gets a link without a usable key. Options, still **OPEN** for the user (Q1, contracts.md change
log F-3): (a) the caller (Resources) rotates through `IssueNewLinkKeyAsync` on a replay and returns the new key; the
first key stops working, so a client that did receive the first response and retries anyway ends up with a dead key;
(b) keep the plain key encrypted (Data Protection) next to the idempotency record for 24 hours only, so the replay is
exact (needs an addendum §5.1.2/§16.7 change); (c) keep the built behaviour (no key on replay). No endpoint calls the
issuer yet; decide before the Stage 2 snippet and upload-complete endpoints.

`IContentLinkCommands`: each method loads the link with `target_kind <> 0` (else `not_found`), runs one transaction
with `SET LOCAL lock_timeout = '3s'`, writes the C10 outbox row in the same transaction, then removes the cache entry
and calls `invalidator.NotifyChangedAsync` (as `RenewAsync` does, `LinkService.cs:568-569`). `RenewAsync` follows
`LinkService.RenewAsync` (`:529-570`) except: the expiry comes clamped from the caller; only `Active` or `Quarantined`
links renew; a guest link never renews (Resources refuses before calling). Burn and purge refusals are Resources'
checks (ARC-006).

## 6. Every URL-only site and its branch

| # | Site | Today | Change for content kinds |
|---|---|---|---|
| 1 | `LinkService.CreateAsync` / `CreateGuestAsync` (`LinkService.cs:72`, `:267`) | URL creation | Unchanged; `LinkInput` stays URL-only (validator `LinkInputValidator.cs:12` keeps `DestinationUrl` required). Content goes through the issuer only (§16.9). |
| 2 | `LinkService.ComputeRequestHash` (`:475-481`) | hashes `DestinationUrl` | Unchanged (URL path only). |
| 3 | Monthly cap (`:96-103`) counts every row of the owner | | `CountAsync(l => ... && l.TargetKind == LinkTargetKind.Url)` only once PD-08 is approved. Until then: OPEN, see Q3. Not reachable in Stage 1 (no content rows). |
| 4 | `UpdateAsync` (`:490`), `SetFlagsAsync` (`:571`), `RenewAsync` (`:529`), `DeleteAsync` (`:666`), owner routes `/api/links/{code}*` | URL edit paths | Refuse content links with `409 content-link` ("manage this through /api/resources"), before any change. `UpdateAsync` would otherwise turn a content link into a URL link. Content changes go through `IContentLinkCommands`. |
| 5 | `ListAsync` search `ILike(l.DestinationUrl, pattern)` (`:626-628`) | | Null-safe (`l.DestinationUrl != null && ...`). Default filter `TargetKind == Url` until Q2 is answered, so `GET /api/links` is unchanged. |
| 6 | `GetAsync` (`:607`), `ToDto` (`:1037-1058`) | | `ToDto` fills `TargetKind`, `ResourceId`; `DestinationUrl` null for content. |
| 7 | `LinkResolver.FindPublicStatsAsync` (`LinkResolver.cs:46-53`), `PublicLink.DestinationUrl` | public stats page shows the destination | Content: `PublicStats` is always false at issue, so not reached; make `PublicLink.DestinationUrl` nullable with BE-007. |
| 8 | `AdminFindBySlugAsync` / admin DTO (`LinkService.cs:713-730`, `AdminLinkDto`) | | Nullable destination/host, add kind and resource id (§2). |
| 9 | `ReinstateAsync` re-checks reputation and blocklist (`:751-760`, `reputation.CheckAsync(link.DestinationUrl)` and `blocklist.IsBlockedAsync(link.TargetHost, ...)`) | | Skip both for content kinds; reinstating content also needs Resources to restore bytes from quarantine (ARC-006 T14, C10 `LinkEnabled`). |
| 10 | `AdminBulkTakedownAsync` filter `TargetHost` (`:824-861`) | | Null rows never match a host filter (no change needed). C12 adds `sha256` and `target_kind` filters. `blockDestination` only for URL. |
| 11 | `TakedownService.TakeDownAsync` evidence `TargetUrl = link.DestinationUrl` (`AbuseControls.cs:111-119`) | `TargetUrl` required (`LinksDbContext.cs:129`) | Content: `TargetUrl = ""` is not acceptable evidence; make the column nullable and add `resource_id`, `sha256`, `object_key` (C12, expand-only). Content takedown also moves bytes to quarantine via the outbox (`LinkDisabled` abuse/safety). |
| 12 | `SafetyRecheckJob.RunOnceAsync` claim SQL (`AbuseControls.cs:164-173`), `reputation.CheckAsync(link.DestinationUrl)` (`:187`) | | Add `AND target_kind = 0` to the claim query (addendum §13.5). |
| 13 | `PurgeJob` archive `link.DestinationUrl` (`PurgeService.cs:102-106`) into `LinkDailyStatRow.TargetUrl` (required, `AnalyticsDbContext.cs:56`) | | Pass `null` for content; Analytics makes `target_url` nullable and adds `target_kind smallint default 0` to the archive (expand-only, Analytics owner). Purge of a content link also emits `LinkPurged` (C10). |
| 14 | `LinkExportSource.ListAsync` (`LinkDigestSource.cs:12-30`) → `LinkExportRow.TargetUrl` → `IdentityService` export (`IdentityService.cs:494-495`) | | Nullable `TargetUrl` + `Kind` (§2). |
| 15 | `LinkDigestSource` email stages (`LinkDigestSource.cs:41-80`) | | Unchanged: content links are links and get the same owner emails; guest content never qualifies (`OwnerId != null`). Email text must not print a destination for content. |
| 16 | `ResolutionService.ResolveAsync` (`ResolutionService.cs:62-103`) | gate → preview → redirect | See §7. |
| 17 | `ResolutionService.UnlockAsync` (`:106-125`), `POST /api/unlock/{slug}` (`ResolutionEndpoints.cs:73-79`) | returns a URL | Content kinds → `not_found` (content unlock is `POST /api/public/content/{code}/unlock`, ARC-004). |
| 18 | `RoutingEvaluator.Choose` (`RoutingEvaluator.cs:38` returns `link.DestinationUrl`), `UtmTags.FromUrl` (`ResolutionService.cs:139`) | | Never called for content kinds. |
| 19 | Crawler preview (`ResolutionService.cs:78-82`, `LinkPreview.Destination`) | meta-refresh to the destination | Content links have no Open Graph input in Phase 1, so `HasOpenGraph` is false; the content preview is ARC-003 (public, ready content only). |
| 20 | `LinkResolver.LoadAsync` (`LinkResolver.cs:73, 88, 122`) | non-null read | §4. |
| 21 | `BulkLinkImporter` (`BulkLinkImporter.cs:21-38`) | URL rows | Unchanged (URL only). |

## 7. Redirect for content kinds (addendum §3, §17)

In `ResolutionService.ResolveAsync`, right after `gate.Evaluate` (`ResolutionService.cs:69`), when
`link.TargetKind != Url`:
1. `Purgeable` → `NotFound`; `Deleted` → `Deleted`; `Disabled` → `Disabled` (with `DisabledKind`); `Expired` →
   `Expired` with `Url = null` (content links have no fallback, so the endpoint shows the 410 page,
   `ResolutionEndpoints.cs:61-62`).
2. `Allowed` and `Locked` both → `ResolveKind.Content`, `Url = {Frontend:BaseUrl}/{p|i|f}/{Uri.EscapeDataString(slug)}`
   (`ViewerPathSegments`). The redirect never checks the password or the key (§3.3); the viewer does.
3. No click consumption, no PD-47/PD-48 click-limit branch (content `MaxClicks` is always null, §3.7), no Open Graph.
4. GET records a `ClickEventKind.Redirect` event for account content (owner analytics, addendum §12); guest content
   increments only the guest counter (`ResolutionService.cs:152-156`). HEAD records nothing and answers the same 302.
5. Endpoint (`ResolutionEndpoints.cs:56-67`): `ResolveKind.Content => 302 Location: result.Url` with
   `Cache-Control: private, max-age=0`, no `Set-Cookie`, no fragment in `Location`.

## 8. Review checklist (BE-007)

- [ ] Both migrations apply on a copy of a Phase 0 database; `\d links.links` shows the three constraints `VALID`;
      the unique index is `VALID`.
- [ ] All Phase 0 tests pass unchanged (URL behaviour identical).
- [ ] Cache key `link:v2:`; `LinkCacheInvalidation.cs:98` uses `LinkCache.Key`.
- [ ] A content link created through `ILinkIssuer` has null destination/host, a key hash when requested, no
      `MaxClicks`, no fallback, purge rule per owner type; no Web Risk call, no blocklist query (assert with fakes).
- [ ] Phase 0 owner write routes return `409 content-link` for a content link and change nothing.
- [ ] `GET zaa.gl/<content code>` → 302 to the viewer for active and password links; 404/410 pages for closed ones;
      HEAD identical without analytics; no `Set-Cookie`.
- [ ] Safety re-check, purge archive, export, admin views and takedown handle a null destination (no exception).
- [ ] Every `IContentLinkCommands` call writes its C10 outbox row in the same transaction.

## 9. Questions for the user

- Q1 (OPEN) Retry of a share request and the secret key. Built today: a retry returns the link without a key (§5,
  "Idempotent replay"). Options: (a) a retry gets a new key and the first one stops working (complies with today's
  rule, rare edge case); (b) keep the key encrypted for 24 hours only so a retry returns the same key (needs an
  addendum change to §5.1.2/§16.7); (c) keep the built behaviour. Recommendation: (b), because the desktop app retries
  on flaky networks and a silently dead link is worse than a 24-hour encrypted copy.
- Q2 Should content shares also appear in the main link library (`/api/links`, the dashboard list), or only in a
  separate "My content" list? Recommendation: one library with a kind filter (PRD "one library"), shipped with Stage 2;
  until then `/api/links` shows URL links only.
- Q3 PD-08 (PROPOSED): do content shares count toward Free's 50 links per month? Recommendation: no. Needed before
  Stage 2 content goes live.
