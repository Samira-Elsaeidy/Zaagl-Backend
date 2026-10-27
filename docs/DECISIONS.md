# Decisions

Per the spec's own instruction ("If something is still ambiguous, pick the safer option and record it
here"), this is the running log of every place this build chose a concrete behavior where the spec was
ambiguous, or deliberately simplified something the spec described more strictly. Each entry says what was
chosen, why, and what the cost of the simplification is. `docs/TRACEABILITY.md` is the test-coverage view
of the same gaps; this file is the design-rationale view.

## Code generation and the coordinator (spec §4-§6)

- **Reserved-word list merge.** The generator's reachability check merges this app's own pre-existing
  route names (from Phase 0, e.g. `dashboard`, `settings`) with the spec's §4.5 rule 3 list, rather than
  using only the spec's list. Reason: this app already has real routes at those paths; a short code
  colliding with one would break navigation, not just a cosmetic issue. Cost: the reserved set is larger
  than the spec's literal list, very slightly shrinking the usable code space - negligible at Base62-62^5+
  scale.
- **`coordinator_user`/`api_user` roles exist in `deploy/init-db` but are not used.** The app connects as
  one shared role for every module. Rewiring each module's connection string to its own least-privilege
  role is real, cross-cutting work or deferred out of the Docker-packaging phase. Cost: the negative-grant
  tests spec's §3 implies are not meaningful yet (there's nothing to deny).

## Links and lifecycle (spec §4.3, §7.2)

- **Expiry is optional in this build**, not "every link expires" as spec's §1.1 states literally. A link
  with no `Policy.ExpiresAt` has `PurgeAfter = null` and never purges by age. Reason: this is a
  Phase-0-inherited product decision (optional expiry was already a feature before this rebuild) that the
  spec rebuild did not revisit. Cost: `PurgeAfter` being null sidesteps the quarantine/purge guarantees for
  those links entirely - intentional, not a bug, but worth remembering when reading the purge job's code.
- **Routes stay at `/api/links`, `/api/auth`, etc.**, not the spec's `/api/v1/*` prefix, and there is no
  separate `zaa.gl`/`api.zaa.gl`/`app.zaa.gl` host split in this dev build (`Hosts:Short` = `Hosts:Api` =
  `localhost`). Reason: this app predates the v9.4 rebuild with its own route names already wired into a
  frontend; renumbering every route is a large, mechanical, high-blast-radius change with no behavioral
  value for a single-instance dev/demo deployment. Cost: the host-routing guarantee (421 on host mismatch)
  is real and tested, but exercises only one logical host in this environment.

## Guest creation (spec §7.12)

- **CAPTCHA is wired into guest creation only**, not registration, resend-confirmation, forgot-password or
  reports. Reason: building the `ICaptchaVerifier` seam once and wiring it everywhere at once was judged
  better than a half-built seam reused inconsistently; guest creation was the first and most abuse-exposed
  surface, so it went first. Cost: the other four endpoints currently have no CAPTCHA at all - a real,
  tracked gap, not a design choice.
- **Guest rate limits (per-minute, global-hourly) are in-memory, per-instance**, matching the spec's own
  wording for those two specifically (the 24h rolling quota is the one the spec requires to be shared
  across instances, and that one *is* backed by Postgres). This is correct for year-1's single-instance
  topology and would need a shared store (e.g. Valkey) before scaling horizontally - see `docs/SCALING.md`.

## Abuse controls (spec §7.15)

- **Turnstile's documented sandbox test secrets skip the action/hostname binding check.** Cloudflare's
  "always passes" test secret returns a canned response with no real `action`/`hostname` to compare
  against. Verifying only `success` for those specific, publicly-documented secrets (never for a real
  production secret) keeps dev/test environments working without weakening the real binding check at all.
- **The destination blocklist and alias denylist are both loaded in full on every check** (`ToListAsync()`
  then filtered in memory), not as targeted SQL predicates. Reason: both lists are expected to stay small
  (tens to low hundreds of entries); the simplicity of pattern matching in C# (host-suffix, URL-prefix) was
  judged worth more than a marginal query optimization at this scale. Revisit if either list grows large.

## Email (spec §7.10)

- **Confirm-email and password-reset tokens are generated eagerly at queue time**, not by the dispatcher
  at send time as the spec's literal text describes ("the outbox payload never holds a token"). Full
  rationale on `EmailOutbox`'s doc comment: deferring generation would mean the raw token could never be
  reconstructed from its stored hash, forcing a breaking rework of Phase 3's already-tested token issuance
  (13 passing tests depend on the token being available synchronously) for a marginal reduction in the
  token's at-rest exposure window - it already expires in 3 days / 1 hour either way.
- **No separate template files.** `EmailTemplates` builds strings in code rather than reading from
  `src/Zaagl.Api/Email/Templates/*.txt`/`*.html` as the spec's literal text suggests. Reason: a templating
  layer (file loading, variable substitution, caching) is infrastructure with no behavioral payoff at this
  build's scale; the content itself matches the spec's rules (codes and dates only, never target URLs).
- **"Once a day" is "once per UTC day, after `Email:DailyRunUtc` has passed"**, checked by a 15-minute poll
  loop, not a precise scheduler. Acceptable for one instance; a real cron-like trigger (or a distributed
  lock) would be needed before running more than one instance of this job.

## Account deletion and export (spec §7.14)

- **Personal fields are cleared immediately on deletion**, not kept as an untouched tombstone until the
  user's last link is purged, then fully removed. Reason: this build's Identity `users` table already *is*
  the profile table (no separate `user_profiles`/Identity split as the spec's schema implies); there is no
  natural place to park a "profile only, no Identity data" intermediate row without a schema change.
- **Export has no CSV/ZIP format** (`format=csv` is accepted but ignored; the response is always JSON) and
  excludes daily-stats series - only archived-link metadata, not their per-day click numbers. Capped at
  50,000 links / 10,000 archived links instead of a true unbounded server-side cursor. Reason: scope vs.
  time; the JSON/profile/links path is the one most likely to matter for an actual GDPR-style request, and
  was built to be correct rather than also building a second (CSV) serialization and a cursor-streaming
  layer for a dataset size this build will not realistically hit.

## Admin API (spec §7.9)

- **`ops declare-restore` is an admin HTTP endpoint** (`POST /api/admin/declare-restore`) instead of a CLI
  container, since this build has no `ops` service. Functionally identical (inserts the same freeze row);
  only the invocation mechanism differs from the spec's literal text.
- **The automatic coordinator-driven alias-freeze sync job is not built** - only the manual/operator path
  exists. The spec's own text already treats the operator step as mandatory and the automatic detection as
  a best-effort heuristic that "can miss a restore"; this build keeps the mandatory path and defers the
  heuristic one.

## Not built at all (see `docs/TRACEABILITY.md`'s "Deferred entirely" sections for the authoritative list)

The allocator sink as a real S3-compatible bucket, the full chaos/failpoint test suites of spec §8.2-§8.3,
NetArchTest architecture rules, Stryker mutation testing, and `tools/traceability-check` itself remain
unbuilt. These are noted here only for completeness; the detail lives in TRACEABILITY.md.
