# ADR-0009: Guests may create snippets and uploads under guest limits

Status: Accepted 2026-11-09 (PD-14, changed from "no guest content"; limits updated later the same day). Addendum §11.

## Context
Phase 0 allows guest links (spec §7.12) with CAPTCHA and per-IP-partition quotas. The draft recommended requiring an
account for content (PRD risk register).

## Decision
- Guests may create snippets and upload files and media.
- CAPTCHA (Turnstile) on snippet create and upload-session create.
- Rate limits per IP partition (IPv4 address, IPv6 /64): 30 per hour and 60 per rolling 24 hours, snippets and uploads
  counted together, hard limits in locked hourly buckets. /56 and /48 caps proportional, values to confirm.
- 7-day expiry, no quarantine (bytes deleted at expiry), no statistics, no management, no emails.
- Guest per-file cap: proposed 25 MB, to confirm.

## Consequences
- The limits depend on a trustworthy client IP: they are enforceable only after BE-030 (forwarded headers trusted only
  from exact known proxies). Before that QA records them BLOCKED.
- Larger abuse surface on a `zaa.gl` subdomain (ADR-0003); the abuse controls of addendum §13 apply to guest content.
- Open: whether guest URL links share this budget (separate counters until decided); guest burn and password.

## Rejected options
- No guest content (draft): rejected by the product owner.
- Spec §7.12 numbers (20 per 24 h) for content: superseded by the 2026-11-09 update.
