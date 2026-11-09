# ADR-0012: Only BE-001, BE-030 and BE-031 are fixed in Stage 1

Status: Accepted 2026-11-09 (PD-24, partial). Addendum §15.

## Context
Phase 1 depends on several Phase 0 defects: unenforced API-key scopes, forwarded headers trusted from anyone, an
unthrottled redirect that accepts 3-character paths, and a shared test database.

## Decision
Fix in Stage 1: BE-001 (scope enforcement), BE-030 (trust `X-Forwarded-For` only from exact known proxies), BE-031
(spec §7.3 redirect and tiered miss limits; 404 for 1–3 character and multi-segment paths before I/O). Test isolation
(QA-001..003) is deferred.

## Consequences
- Guest limits (ADR-0009) and every IP limiter become trustworthy only after BE-030.
- The shared test database can cause flaky failures; QA records them and never retries them away.

## Rejected options
- Defer all Phase 0 fixes.
- Fix everything, including test isolation, now (rejected for schedule).
