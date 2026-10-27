# Abuse handling

How Zaagl detects, reports, and takes down abusive links. For the underlying mechanisms (blocklist,
takedown service, safety re-check job), see `docs/TRACEABILITY.md`'s abuse-controls section; this document
is the moderation workflow and response targets for the TechTroll team.

## How abuse surfaces

1. **Automatic destination checks.** Every link creation and edit is checked against Google Web Risk
   (malware, social engineering, unwanted software) and the destination blocklist (`blocked_destinations`:
   exact host, host-suffix, or URL-prefix patterns). An unsafe or blocked destination is refused outright
   (`400 destination-unsafe` / `destination-blocked`) - it never becomes a live link.
2. **The destination re-check job** (every 6 hours) re-screens links that are new, recently clicked, or
   have never been checked, within the shared monthly lookup budget. A verdict that flips to unsafe after
   creation disables the link automatically (`disabled_kind: "safety"`) via the same takedown routine used
   for manual abuse disables.
3. **Public reports**: `POST /api/reports { code?, url?, category, details?, reporterEmail? }` - anyone
   can report a link or URL, no account needed (CAPTCHA-gated in production; not yet wired in this build,
   see `docs/TRACEABILITY.md`). Reports land in `abuse_reports` with status `open`.

## Response targets

| Category | Target |
|---|---|
| Phishing, malware | **12 hours** - every report in these categories should raise an immediate alert to the team (alerting itself is not built in this phase; today this means checking `GET /api/admin/reports?status=open` directly) |
| Everything else | **48 hours** |
| Open reports generally | Reviewed weekly regardless of category, so nothing silently ages past its target |

## Moderation workflow (today, via the admin API)

1. List open reports: `GET /api/admin/reports?status=open` (Admin-only, role + TOTP + allowed network).
2. Investigate the reported `code` or `url`: `GET /api/admin/links/{code}` for the admin link view
   (owner, guest-creator-key, target host, safety-check timestamp, full disabled/deleted state - none of
   which the owner's own view exposes).
3. **Take the link down**, if warranted:
   `POST /api/admin/links/{code}/disable { "reason": "...", "kind": "abuse" }`. This sets `disabled_kind`
   (overwriting any prior `admin`/`user-disabled` state, so the owner re-enabling it later can't undo a
   takedown), blocks the alias permanently if it's a custom one (`blocked_aliases` - never released, even
   after purge, until an explicit reinstate), and writes an `abuse_evidence` row kept for
   `Abuse:EvidenceRetentionDays` (default 90).
4. **For a pattern across many links** (same destination host, same guest creator key, same owner, or a
   creation-date window), use a bulk takedown instead of disabling one at a time:
   `POST /api/admin/takedowns { "filter": { "targetHost": "..." }, "reason": "...", "blockDestination": true, "dryRun": true }`.
   Always run with `dryRun: true` first to see `linksAffected` before committing. Set `blockDestination`
   to also add the host to `blocked_destinations` so future creation attempts are refused outright.
5. **Resolve the report**: `POST /api/admin/reports/{id}/resolve { "status": "actioned", "resolution": "..." }`
   (or `"dismissed"` with a reason, if the report didn't warrant action).
6. **If a takedown turns out to be wrong**, reverse it with the audited reinstate endpoint rather than the
   ordinary enable route (`enable` explicitly refuses an abuse/safety-disabled link with
   `409 takedown-active`):
   `POST /api/admin/links/{code}/reinstate { "reason": "..." }`. This re-runs the reputation check and the
   blocklist check before committing, removes the alias from `blocked_aliases` if this link put it there,
   and records the reversal.

## Maintaining the blocklist and denylist directly

Outside of a specific takedown, the team can also manage the standing lists directly:

- `GET/POST/DELETE /api/admin/blocked-destinations` - permanent host/host-suffix/URL-prefix blocks (seeded
  at deploy time from `Safety:DenyRedirectorHosts`, a list of known open redirectors/URL shorteners so
  links can't be chained to hide a destination).
- `GET/POST/DELETE /api/admin/blocked-aliases` - refuses to re-add an alias still held by a live link
  (`409`); use the takedown/disable flow for that instead, which adds the entry as part of the same
  transaction as the disable.
- `GET/POST/DELETE /api/admin/alias-denylist` - exact or prefix-matched slugs an operator never wants
  issued (e.g. a brand name people keep squatting), layered on top of the static reserved-word list every
  slug is already checked against.

## What's not built yet

- CAPTCHA on the public report endpoint (tracked in `docs/TRACEABILITY.md`).
- Automated alerting for phishing/malware reports and for the 24-hours-open threshold - today this is a
  manual check of `GET /api/admin/reports?status=open`.
- The `abuse@` mailbox and its intake process are outside this codebase's scope.
