# Stage 1 review: Architect (ARC-001 contracts, PD-34 spec addendum)

Reviewer: zaagl-head. Date: 2026-11-09. Nothing committed; this review changes no product files.

## Verdicts

| Item | Verdict |
|---|---|
| PD-34 addendum `docs/spec/zaagl-phase1-addendum.md` | ACCEPT WITH NOTES (needs the user's approval of PD-34 to become the QA oracle) |
| ARC-001 contract pack `contracts.md` C1–C12 | ACCEPT WITH NOTES (contract freeze not yet reached: C1 partial, C6 and C11 not stubbed) |
| ADR-0001 … ADR-0012 | ACCEPT |
| Contract stubs (7 files) | ACCEPT WITH NOTES |

No REWORK.

## Addendum vs approved decisions (DECISIONS.md Approvals table)

| Decision | Approved text | Addendum | Match |
|---|---|---|---|
| PD-01 | `#k=` key, on by default for snippets and files; images may be public | §5.1: 128-bit, hash only, default keyed for snippets/files; media default left OPEN | yes |
| PD-02 | viewers `app.zaa.gl/p,i,f`; `zaa.gl/<code>` redirects | §3.2 302 to `app.zaa.gl/{p,i,f}`; `zaa.gl/s,i,f,p` reserved 404 | yes |
| PD-03 | `zaa.gl` subdomain, proposed `usercontent.zaa.gl`; no cookie on it or `.zaa.gl`; `__Host-`; attachment or sandbox CSP; CSRF everywhere | §4.1–4.2 lists all four mitigations plus "no credentials accepted" and abuse exposure | yes |
| PD-04 | Free and Starter only; Free 25 MB/7 d, Starter 100 MB/30 d; totals to confirm; capability flags | §10 | yes |
| PD-05 | first GET of a ticket minted by a button POST; resume 30 min, max 16 requests; previews/HEAD/viewers/owner never consume | §8.2 | yes |
| PD-06 | bytes kept through 90-day quarantine, deleted at `purge_after`; guest deleted at expiry | §9 | yes |
| PD-07 | device authorization for every account; API keys paid-only | §14.1, §14.3 | yes |
| PD-12 | tus on `api.zaa.gl` through Cloudflare, chunks ≤ 90 MB | §6.1 (cap 90,000,000 bytes, client 64 MiB) | yes |
| PD-14 (updated) | 30/hour and 60/day per IPv4 address / IPv6 /64, snippets + uploads together; /56, /48 proportional to confirm; CAPTCHA, 7-day, no quarantine, no stats, no management; guest file cap 25 MB to confirm; depends on BE-030 | §11.1–11.9 | yes, including the 30/60 update |
| PD-19 | Ctrl+Alt+Z / Cmd+Ctrl+Z, rebindable | §14.5 | yes |
| PD-23 | BFF session before content | §4.3 | yes |
| PD-24 (partial) | only BE-001, BE-030, BE-031; QA-001..003 deferred, flaky failures recorded | §15 | yes |

The 5× / 15× proposal for /56 and /48 is exactly spec §7.12's ratio (20 → 100 → 300, spec line 765), so it is a faithful proposal, correctly marked "to confirm".

## Choices the architect made itself

| Choice | Judgement |
|---|---|
| Headers `X-Zaagl-Link-Key`, `X-Zaagl-Grant` | Technical; fine. Keeps the key out of paths and query strings. |
| Request body cap 90,000,000 bytes | Direct reading of PD-12 ("90 MB", decimal). Fine; under Cloudflare's 100 MB. |
| Plain `Guid` ids and duplicated enum values (Resources/Files/Snippets/Media) instead of project references | Reasonable: avoids new module coupling. Risk: `LinkTargetKind` and `ResourceKind` values drift. Note for ARC: add an architecture test that asserts the duplicated enums have identical names and values. |
| No stubs for C6 (analytics kind) and C11 (native auth) | Reasonable for this round (those files are being edited by BE-001); but the contract-freeze checkpoint (AGENT_TASKS.md "stubs compile, consuming agents have reviewed them") is **not reached** until C1's `LinkSnapshot`/`LinkDto` change, C6 and C11 are stubbed. Must happen before BE-006/BE-007 and before Stage 2 starts. |
| Download ticket valid 120 s if unused; inline tickets 15 min; grants 10 min; scan retry 1/5/15/60 min, 6 attempts, then reject | Technical defaults, not product decisions, provided each is configuration (addendum says so for some, not all). Note: with "reject after 6 attempts", a scanner outage of a few hours permanently rejects the uploads made during it; acceptable fail-closed behaviour but the owner should see a "try again" message. |
| §3.7 "the redirect of a content link MUST NOT consume a click limit" until PD-10 | Safe interim default; it means `MaxClicks` does nothing on content links until PD-10 is decided. Covered by the PD-10 question. |
| §11.4 "the global guest ceiling of spec §7.12 applies" to content | Plausible but it reuses a URL-link ceiling for content; fold into the "do guest URL links share the budget" question. |

None of these needs the user except the ones already listed as open below.

## Stubs

- Types, enums, interfaces and `const` wire names only. The only expression body is `ContentOwner.IsGuest => UserId is null` (trivial, acceptable). No method bodies, no I/O.
- References: no `.csproj` was changed. Access.Contracts uses `Zaagl.Modules.Links.Contracts` only (Access already referenced Links in Phase 0). Resources/Files/Snippets/Media reference only SharedKernel. Billing contracts use only Billing types. No module touches another module's non-Contracts code.
- Notes for the owning module agents: `UploadProtocol.MaxChunkBytes` and `SessionIdleHours` are `const`, so their values are compiled into consumers; keep the server-side enforcement in options, and treat these as client defaults. `ContentPlan.Tier` is exposed; addendum §10.2 forbids tier comparisons outside Billing, so add it to the NetArchTest/analyzer rule.
- Build claim ("per-project builds, 0 errors") not re-run by the reviewer (other agents are building); NOT_RE-VERIFIED, reasonable to accept on the agent's evidence for stubs of this kind.

## Status mismatches for the lead to fix in DECISIONS.md

- PD-34 is still PROPOSED, but the addendum calls itself the normative oracle. Needs the user's approval (Q1 in the final list).
- Appendix A lists PD-08 and PD-10 as OPEN; DECISIONS.md has them PROPOSED. Align one way.
- PD-26's text says "dev and CI currently test on 17"; dev compose uses `postgres:18` (see stage1-devops.md).

## Pre-launch note (recorded, not for now)

Verify no Cloudflare feature (bot management, challenge, Turnstile) sets a cookie on `.zaa.gl` or on `usercontent.zaa.gl` (addendum §4.2.2). Assign to OPS-001/OPS-010 acceptance.

## Questions for the user

Consolidated with the other Stage 1 reviews in the lead's report; listed here in short form.
1. Approve the addendum as the Phase 1 rulebook (PD-34)?
2. Images: private by default (need the `#k=` key) or public by default? **Answered: PD-37, private by default.** Architect updates addendum §5.1.3 and ADR-0001.
3. `usercontent.zaa.gl`: through Cloudflare or DNS-only? **Answered: PD-38, through Cloudflare with chunked/ranged delivery under the per-request limit.** Architect updates addendum §4.1 and C3; devops OPS-001/OPS-010.
4. Plan values: per-account storage totals (250 MB / 5 GB), guest file cap (25 MB), which plans get password and burn on content.
5. Guest IPv6 /56 and /48 caps (5× and 15×), and whether guest URL links share the content budget.
6. Delete bytes immediately when the owner deletes, or at `purge_after`?
7. Later stages (not needed before Stage 2): snippet size caps (PD-17), what counts as a view (PD-10), burn-after-view for snippets, CAPTCHA on burn, owner email on burn, PD-09, PD-11, PD-13, PD-16, PD-33.
