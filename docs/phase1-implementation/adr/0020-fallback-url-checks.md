# ADR-0020: Fallback URLs get the same checks as destinations

Status: Accepted 2026-11-09 (PD-44, product owner). Addendum §17.3. Contract C3. Extends ADR-0016.

## Context
Under PD-42 `zaa.gl` sends visitors of an expired link to the owner's fallback URL. Phase 0 checks only its syntax
(`LinkService.cs:914-918`), so a fallback could point at a phishing or malware page that a destination would be refused
for, and expired links would become an unchecked redirector.

## Decision
- When a fallback URL is set or changed it passes the URL rules, the destination blocklist and the Web Risk reputation
  check of spec §7.2 step 5, exactly like `target_url`; a failure is refused the same way.
- Afterwards it is covered by the Web Risk re-check and blocklist takedown of spec §7.15 like a destination.

## Consequences
- Backend extends the safety pipeline and re-check job to the fallback column; QA tests a blocked and a flagged fallback.

## Rejected options
- Syntax check only: makes the fallback an unchecked open redirect on `zaa.gl`.
