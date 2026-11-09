# ARC-004 · Access gate as a contract, link key, access grants

| | |
|---|---|
| Owner | zaagl-architect. Implementer: backend (Access module; Links for the key column). |
| Status | DESIGN, ready to implement. Contract C4 frozen (`src/Modules/Access/Contracts/AccessContracts.cs`). |
| Rules | Addendum §3.3, §3.5, §5, §8.2.5, §16 items 6-8. PD-01, PD-37, PD-45 (APPROVED). ADR-0001, ADR-0014, ADR-0021. |
| Not covered | Max views / max downloads for content: not in Phase 1 until PD-10 is decided (PROPOSED). |
| Error codes | contracts.md C9 catalogue (single list). |

## 1. Components

| Piece | Module | Notes |
|---|---|---|
| `link_key_hash bytea` on `links.access_policies` | Links | Migration A of ARC-002. Only the SHA-256 is stored. |
| `LinkSnapshot.LinkKeyHash`, `PasswordHash` | Links (C1) | Read by the gate from the cached snapshot. Caching a hash in Valkey is allowed (§16.7 forbids the key, not its hash). |
| `ILinkKeys` | Access | `LinkKeys` (pure, singleton). `Matches` is used by the gate and unlock. **Not** used by the issuer: Links cannot reference Access (Access references Links.Contracts), so the Links issuer mints the key itself (`src/Modules/Links/Application/LinkService.Content.cs:69-77`). Both sides follow the one format in §2 and contracts.md C4 "Link key format" (change log F-2). `Generate` has no Stage 1 caller. |
| `IAccessGate` | Access | `ContentAccessGate`. Phase 0 `Access.Application.AccessGate` stays for the URL redirect until Resolution moves (QA-014 later). |
| `IAccessGrants` | Access | `AccessGrantService`. Key ring `Access:GrantKeys`. |

Dependency direction stays as contracts.md shows: Access references Links.Contracts only; Resources and the API
reference Access.Contracts. Access does not see resource state; see §3.

## 2. Link key (PD-01)

- Generate (built in the Links issuer, `LinkService.Content.cs:74-76`): 16 bytes from
  `RandomNumberGenerator.GetBytes(16)`; plain key = `Base64Url.EncodeToString(bytes)` (`System.Buffers.Text.Base64Url`,
  RFC 4648 §5 alphabet, no padding, 22 chars); stored hash = `SHA256.HashData(bytes)`: plain SHA-256 of the 16 raw
  bytes, not of the string; no pepper, salt or HMAC key; 32 bytes in `links.access_policies.link_key_hash`.
- Match: the presented header value must be exactly 22 characters of the base64url alphabet and decode to 16 bytes;
  anything else is a wrong key. Compare `SHA256(decoded)` with the stored hash using
  `CryptographicOperations.FixedTimeEquals`. Missing or wrong key on a keyed link → `NotFound`.
- Transport: header `X-Zaagl-Link-Key` only (`AccessWireNames.LinkKeyHeader`). The viewer reads `#k=` with script,
  calls `history.replaceState` to drop it from the address bar, and sends it to the BFF, which forwards the header to
  the API (the BFF header allow-list must include `X-Zaagl-Link-Key` and `X-Zaagl-Grant`).
- Never logged: ASP.NET HTTP logging must not log these two headers (`RequestHeaders` allow-list, default excludes
  unknown headers; keep it that way), the Nginx `log_format` for `api.zaa.gl` and `app.zaa.gl` must not include them,
  and problem bodies never echo them.
- Equal timing class: an unknown code and a keyed link with a wrong key both resolve through the cached snapshot
  lookup (negative results are cached too, `SnapshotEnvelope`), then the gate does one hash compare for keyed links
  and a dummy compare against a fixed 32-byte value for unknown codes. Both answer the same `404` problem body.

## 3. Gate evaluation (`IAccessGate.EvaluateAsync`)

Input: code, `AccessCredentials(LinkKey, GrantToken)`, `AccessPurpose`. Time from `TimeProvider`. Never consumes
anything (clicks, views, burns).

1. Snapshot by `SlugRules.Key(code)` through `ILinkResolver.GetSnapshotAsync`. None → `NotFound`.
2. Link state (`LinkStateCalculator.Compute`, `LinkContracts.cs:181-200`), plus `IsActive == false` treated as
   disabled exactly as the Phase 0 gate does (`src/Modules/Access/Application/AccessGate.cs:46-48`):
   `Purgeable` → `NotFound`; `Deleted` → `Gone`; `Disabled` → `TakenDown` when `DisabledKind` is `abuse` or `safety`,
   else `Disabled`; `Quarantined` → `Gone`. The result carries `LinkState` (contract field added this round) so the
   caller can apply addendum §2.4, where `burned` (resource) ranks above `expired` (link).
3. Purpose `Redirect` stops here: `Allowed` (content redirect never sees the fragment, addendum §3.3).
4. Purpose `View`, `Raw`, `Preview`, `Download` on a URL link (`TargetKind == Url`) → `NotFound` (content endpoints
   never serve URL links).
5. Link key: if `LinkKeyHash != null` and the presented key does not match → `NotFound` (§2).
6. Password: if `PasswordHash != null`, the grant must validate for this link and purpose (§4) and its
   `PasswordStamp` must equal the stamp of the current `PasswordHash`; otherwise `PasswordRequired`.
7. `Allowed` with `LinkId`, `ResourceId`, `Kind`, `IsGuest = OwnerId == null`.

Resource state is the caller's step: the content endpoint (Resources, composed in the API) calls the gate, then
`ResourceStateCalculator.Compute(gateResult.LinkState, resource, now)` (ARC-006 §2). Order of the combined result is
exactly addendum §2.4. Because the gate checks link state before the key, a keyless request to an expired keyed link
gets `410`; the redirect on `zaa.gl` already reveals the same, so nothing new leaks.

Responses (API layer): `NotFound` → `404 not_found`; `Gone` → `410` with code `expired` (link state `Quarantined`) or
`deleted`; `Disabled` → `410 disabled`; `TakenDown` → `410 taken-down`; resource state `Burned` → `410 burned` (the
viewer picks the page from the code; addendum §17 wording; BE-002 maps every one of these codes to 410); `PasswordRequired` →
`401 password-required` (no `WWW-Authenticate` challenge for bearer, because this is not a credential failure).

## 4. Access grants

### 4.1 Unlock

`POST /api/public/content/{code}/unlock { password }`, link key header required for keyed links. Order:
1. Gate steps 1-5 with purpose `View` (state, key). A wrong key → `404`, the password is not checked.
2. No password on the link → `400 validation` ("this content has no password").
3. `BCrypt.Verify(password, PasswordHash)`; wrong → `401 password-required` (same code as a missing grant; the viewer
   shows "wrong password" because it just submitted one).
4. Issue a grant: purposes `{View, Raw, Preview, Download}`, `ResourceId` from the snapshot, `IssuedAt = now`,
   `ExpiresAt = now + Access:GrantMinutes` (default 10), `PasswordStamp` = first 4 bytes of
   `SHA256(UTF8(PasswordHash))`, read big-endian as `int`.
5. Response `{ grant, expiresAt }`, `Cache-Control: no-store`.

Rate limit: the existing `unlock` policy partitions by IP and `RouteValues["slug"]`
(`src/Zaagl.Api/Program.cs:93`). The content route parameter is `{code}`, so either name the content route parameter
`slug` or extend the partition key to `RouteValues["code"]`; otherwise the per-code part is silently empty and only
the per-IP part applies. BE-030 must be live for the IP part to mean anything.

### 4.2 Token format (shared shape with delivery tickets, ARC-008)

```
token   = b64url(payload) "." b64url(mac)
mac     = HMAC-SHA256(key[kid], "zaagl/grant/v1\n" || payload)        (32 bytes)
payload = 0x01                                  version
          u8 kidLength (1..16) || ASCII kid
          16 bytes link id                      (Guid, RFC 4122 byte order)
          16 bytes resource id                  (all zero = none)
          u8 purposes bitmask                   bit (1 << (int)AccessPurpose): View=2, Raw=4, Preview=8, Download=16
          i64 issuedAt (Unix seconds, big-endian)
          i64 expiresAt (Unix seconds, big-endian)
          i32 passwordStamp (big-endian)
```

The domain string differs from tickets (`"zaagl/ticket/v1\n"`) and the key rings differ (`Access:GrantKeys` vs
`Content:TicketKeys`), so a grant can never be accepted as a ticket or the reverse. `Redirect` (bit 1) is never set.

### 4.3 Validation (`IAccessGrants.Validate`, pure)

Reject unless all hold: token length ≤ 256 characters; exactly one `.`; both parts valid base64url; payload length
matches the layout; version `0x01`; `kid` present in the key ring; MAC equal (`FixedTimeEquals`); `issuedAt ≤ now +
60 s` (clock skew); `now < expiresAt`; `expiresAt - issuedAt ≤ Access:GrantMinutes`; link id equals the argument;
purpose bit set. Failure → `Result` error `password-required`. The gate additionally compares `ResourceId` with the
snapshot's and `PasswordStamp` with the current one (§3.6).

### 4.4 Keys

`Access:GrantKeys`: a list of `{ kid, key }` entries, key = 32 random bytes base64; the first entry signs, every entry
verifies. Rotation: add a new first entry, deploy, remove the old entry after `Access:GrantMinutes`. Secrets come from
the deployment secret store like the JWT signing keys (`deploy/secrets/README.md`), never from `appsettings.json`.
Startup validation fails when the ring is empty or a key is shorter than 32 bytes.

## 5. Threat model

| Threat | Control |
|---|---|
| Guessing a short code to reach private content | 128-bit key required (§2); gate answers 404 like an unknown code. Redirect and miss rate limits (BE-031). |
| Key leaking through logs, Referer, crawlers | Fragment never sent by browsers; `replaceState`; header transport; log redaction (§2); viewer `Referrer-Policy: no-referrer` on content routes (ADR-0023). |
| Key leaking from the database | Only SHA-256 stored; constant-time compare. |
| Password brute force | `unlock` rate limit per IP partition and code; BCrypt cost as Phase 0. |
| Grant replay on another link or purpose | Link id and purpose bitmask inside the MAC; resource id compared by the gate. |
| Grant still valid after password change | `PasswordStamp` mismatch → refused. |
| Grant theft | 10-minute lifetime; header, never cookie or URL; `Cache-Control: no-store`. |
| Grant used as a delivery ticket (or reverse) | Different domain string and key ring (§4.2). |
| Token parser abuse | Length cap before decoding; fixed layout; no JSON or compression. |
| Timing oracle wrong key vs unknown code | §2 equal timing class; same body. |
| Burn consumed by preview bots | Gate never consumes; only the first GET of a POST-minted download ticket claims (ARC-006 §5). |

## 6. Review checklist (BE)

- [ ] `LinkKeys`: 22-char output, decode of 21/23 chars or non-alphabet characters is a mismatch, constant-time compare.
- [ ] Gate order: state, key, password; `Redirect` never checks key or password; URL links never pass content purposes.
- [ ] Wrong key and unknown code: same status, same body, same headers.
- [ ] Grant bound to link, resource, purposes, stamp; refused after expiry, after password change, on another link, for
      a purpose outside the mask, with an unknown kid, with a flipped byte anywhere.
- [ ] Key ring rotation: old kid verifies, new kid signs.
- [ ] `X-Zaagl-Link-Key` and `X-Zaagl-Grant` absent from every log sink.
- [ ] `unlock` partition key includes the code on the content route.

## 7. Questions for the user

None new. `PasswordOnContent` is Starter only (PD-54). Still open and owned elsewhere: whether guests may create public
content (addendum §5.1.3, to confirm) and max views/downloads (PD-10).
