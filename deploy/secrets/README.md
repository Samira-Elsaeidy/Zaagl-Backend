# Generating production secrets (spec §3)

Never commit these, never put them in an image, never generate them in a deploy script. Each is a plain
file whose contents become the secret value (Docker secrets, file-based).

| Secret | Generate with |
|---|---|
| `PG_SUPERUSER_PASSWORD`, `APP_OWNER_PASSWORD`, `ALLOC_OWNER_PASSWORD`, `API_USER_PASSWORD`, `COORDINATOR_USER_PASSWORD` | `openssl rand -base64 24` |
| `SCRAMBLE_KEY_V1` | `openssl rand -base64 32` — **escrow two independent off-host copies before `ops init`; this key is permanent data, never rotate it once codes exist (spec §5.3).** |
| `COORDINATOR_TOKEN`, `COORDINATOR_ADMIN_TOKEN` | `openssl rand -base64 32` |
| `GUEST_KEY` | `openssl rand -base64 32` |
| `JWT_SIGNING_KEYS` | one `{kid, key}` entry per active signing key, e.g. `{"kid":"2026-01","key":"$(openssl rand -base64 32)"}` — keep the previous key for a validation-only overlap when rotating |
| `DATA_PROTECTION_CERT` | a real PFX certificate + its password; not yet used by this build (ASP.NET Core Data Protection key-ring encryption is not wired up - see `docs/TRACEABILITY.md`) |
| `ADMIN_BOOTSTRAP_PASSWORD` | a strong passphrase, read once at first start to create the admin account |
| `WEB_RISK_API_KEY` | from the Google Cloud Console (Web Risk API) |
| `TURNSTILE_SECRET` | from the Cloudflare Turnstile dashboard - **not wired up in this build yet** |
| `SMTP_PASSWORD` | from the transactional-email provider |
| `EMAIL_WEBHOOK_SECRET` | `openssl rand -base64 32` |
| `ALLOC_SINK_CREDENTIALS` | access key/secret for the off-host S3-compatible bucket - **not used in dev** (`Allocator:Sink:Type=LocalFileSystem`); required before a real `ops init` |

Dev (`docker-compose.yml` with no `.env`) uses inline placeholder values for all of these so `docker compose
up` works with zero configuration. Never reuse a dev value in staging or production.
