# ADR-0021: Video and screen recordings are private by default

Status: Accepted 2026-11-09 (PD-45, product owner). Addendum §5.1.3. Contracts C1, C4. Extends ADR-0014.

## Context
ADR-0014 (PD-37) made images keyed by default with an owner opt-out, and left video and screen recordings open.
Recordings often show private screens, like screenshots.

## Decision
Video and screen recordings follow PD-37: created with the `#k=` link key; the owner can make one public at creation or
later, with the same key removal / re-issue rules as images.

## Consequences
Every content kind now has the same default (keyed) and the same owner opt-out. Still to confirm: whether a guest may
choose public at creation.
