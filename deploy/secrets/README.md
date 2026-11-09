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

## Phase 1 additions (OPS-014)

Sources: `docs/phase1-implementation/DEVOPS_PLAN.md` §6, `ARCHITECTURE.md` §2.2-2.3, `DATABASE_AND_STORAGE.md`
(BE tickets/grants), `DECISIONS.md` PD-01, PD-03, PD-05, PD-12, PD-23, PD-26. The names below are the
proposed secret file names; the configuration keys the services read them into are fixed by the backend and
frontend tasks that implement each feature (ARC-004, BE ticket work, FE-029). **None of these is wired up
yet** unless the row says so. Store them as files in `/srv/zaagl/secrets/` (owner `root:zaagl`, directory
`0750`, files `0440`); they reach containers only as Docker secrets read by a key-per-file configuration
provider (a backend change that does not exist yet), never as environment variables in the compose file.

### Application keys (backend)

| Secret | Purpose | Generate with |
|---|---|---|
| `DOWNLOAD_TICKET_KEYS` | HMAC-SHA256 key ring for download tickets (`/d/{ticket}`, PD-05) and the signed delivery URLs for the user-content host (`/o/<objectId>?exp&purpose&sig`). Key ring so tickets keep validating across a rotation. | one `{kid, key}` entry per active key, e.g. `{"kid":"2026-11","key":"$(openssl rand -base64 32)"}`; keep the previous key for one ticket lifetime (30 min, PD-05) after rotating |
| `ACCESS_GRANT_KEYS` | HMAC-SHA256 key ring for access grant / viewer tokens returned by the unlock endpoint (link id, resource id, purpose, exp, about 10-15 min). Must be a different key from the ticket keys so one cannot be replayed as the other. | same shape as above: `{"kid":"2026-11","key":"$(openssl rand -base64 32)"}` |
| `UPLOAD_GRANT_KEY` | HMAC key for tus upload-session tokens if uploads are authorised by a grant rather than the session bearer (PD-12: tus on `api.zaa.gl`). Only needed if ARC-009 chooses grants. | `openssl rand -base64 32` |
| `LINK_KEY_PEPPER` | Optional HMAC pepper for storing the hash of the 128-bit `#k=` link key (PD-01). A plain SHA-256 of a 128-bit random key is already infeasible to reverse; a pepper only adds protection if the database leaks without the secrets. Needed only if ARC-004 chooses HMAC over SHA-256. **Rotating it invalidates every stored link-key hash, so treat it like `SCRAMBLE_KEY_V1`: escrow it and never rotate it.** | `openssl rand -base64 32` |
| `VALKEY_PASSWORD` | `requirepass` for Valkey once it is internal-only (PD-26 recommendation, OPS-029). The API connection string then carries `password=`. | `openssl rand -base64 32 \| tr -d '/+='` (Valkey's connection-string parser dislikes `/`, `+`, `=` in passwords) |

### Web front end (BFF, FE-029, PD-23)

| Secret | Purpose | Generate with |
|---|---|---|
| `WEB_SESSION_KEYS` | Keys that encrypt and sign the `__Host-` session cookie of the Next.js BFF (spec §7.13). Ordered list, newest first: the first key seals new cookies, the others only unseal, which allows rotation without logging everyone out. Each key at least 32 bytes. Lives in the frontend container only; the API never sees it. | one key per rotation: `openssl rand -base64 32`; store as a comma-separated list, newest first, and drop the oldest after the session lifetime has passed |
| `TURNSTILE_SECRET` | already listed above. Phase 1 guest content (PD-14) makes it required: guests must pass a CAPTCHA. The Turnstile **site key** is public and is not a secret. | Cloudflare Turnstile dashboard |

### Operations (DEVOPS_PLAN.md §6)

| Secret | Purpose | Generate with |
|---|---|---|
| `CF_API_TOKEN` | Cloudflare API token for certificate automation (Origin CA / DNS-01 on the `zaa.gl` zone, user-content host per PD-03) and the real-IP range refresh. Scope: `Zone:DNS:Edit` on `zaa.gl` only. | Cloudflare dashboard, "Create token" with that single permission and zone |
| `PGBACKREST_REPO_KEY` | `repo1-cipher-pass` for the encrypted pgBackRest repository (OPS-017). **Escrow off-host next to `SCRAMBLE_KEY_V1`: without it the database backups are unreadable.** | `openssl rand -base64 48` |
| `PGBACKREST_S3_KEY`, `PGBACKREST_S3_KEY_SECRET` | S3-compatible bucket credentials for the pgBackRest repository; write access to that bucket only. | from the storage provider |
| `RESTIC_PASSWORD` | Encryption password of the restic file-backup repository (OPS-018). **Escrow off-host next to `SCRAMBLE_KEY_V1`.** | `openssl rand -base64 48` |
| `RESTIC_S3_ACCESS_KEY_ID`, `RESTIC_S3_SECRET_ACCESS_KEY` | restic repository credentials on the VPS: append-only (object lock or rest-server `--append-only`), cannot delete. Prune credentials live on a separate trusted machine, never on the VPS. | from the storage provider |
| `ALERT_WEBHOOK_URL` | Alertmanager receiver (chat or paging webhook). The URL itself is the credential. | from the alerting provider |

### CI only (GitHub Actions secrets, never on the VPS)

| Secret | Purpose | Source |
|---|---|---|
| `DEPLOY_SSH_KEY` | SSH key for the `deploy` user, restricted with a forced command to `deploy/scripts/deploy.sh` (OPS-016). | `ssh-keygen -t ed25519 -C zaagl-deploy -f deploy_key` (put the public half in `authorized_keys` with `command="..."`) |
| `TAURI_SIGNING_PRIVATE_KEY`, `TAURI_SIGNING_PRIVATE_KEY_PASSWORD` | Tauri updater signature (desktop). | `npx @tauri-apps/cli signer generate -w zaagl-updater.key` |
| `MINISIGN_KEY` | Signature over CLI `SHA256SUMS`. | `minisign -G -p zaagl.pub -s zaagl.key` |
| Apple Developer ID certificate + App Store Connect API key | macOS signing and notarisation (PD-20). | Apple Developer account **[procurement]** |
| Windows signing | Azure Trusted Signing via OIDC (no exported key) or a cloud-HSM certificate (PD-20). | signing provider **[procurement]** |

The CI workflow `.github/workflows/backend-ci.yml` uses only throwaway values (`zaagl_dev`, a `ci-only-...`
JWT key) for its disposable database; it needs none of the secrets above.
