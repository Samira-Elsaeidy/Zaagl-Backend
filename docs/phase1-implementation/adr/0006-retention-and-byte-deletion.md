# ADR-0006: Bytes are kept through the quarantine and deleted at purge_after

Status: Accepted 2026-11-09 (PD-06, changed: the draft deleted bytes at expiry). Addendum §9.

## Context
Account links enter a 90-day quarantine after expiry (spec §1.1) during which the owner can renew. Deleting bytes at
expiry would make renewal of content meaningless.

## Decision
- Content links always expire at `min(requested, now + plan retention)`.
- Account content: at expiry the link answers 410 and bytes are kept, unreachable, until `purge_after`; renewal during
  quarantine is allowed (spec §7.4 rules) and moves `purge_after`. Bytes are deleted at `purge_after`.
- Guest content: no quarantine; bytes deleted at expiry.
- Burn and takedown override this.

## Consequences
- Disk sizing must include up to 90 days of expired content per account (it counts against the quota until deleted).
- Deletion runs through a crash-safe outbox; lag over 15 minutes alerts.
- To confirm: deleting bytes immediately when the owner deletes the link (a deleted link is not renewable).

## Rejected options
- Delete bytes at expiry and keep only the link row quarantined: no renewal of content; rejected by the product owner.
