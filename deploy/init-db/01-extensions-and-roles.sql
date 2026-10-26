-- Spec §4.1/§3: run by the superuser on first Postgres start (docker-entrypoint-initdb.d).
--
-- Deliberate scoped deviation (documented, not silently skipped): the app today connects as the single
-- `zaagl` superuser-ish role for every module (identity/links/analytics/billing all share one connection
-- string). The spec wants three separate schemas (app, identity, alloc) each with their own owner role
-- (used only by migrations) and a narrower runtime role (api_user, coordinator_user) with least-privilege
-- grants, enforced by negative-grant tests. Rewiring every DbContext's connection string to its own
-- runtime role is a real, somewhat invasive change across every module's *Module.cs DI registration - out
-- of scope for this Docker-packaging phase. This script creates the roles now (so the schema/role shape
-- exists and the extension prerequisite is met), but nothing uses coordinator_user/api_user yet; the
-- coordinator still connects as `zaagl` too. See docs/TRACEABILITY.md for this gap.

CREATE EXTENSION IF NOT EXISTS btree_gist;

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'api_user') THEN
        CREATE ROLE api_user LOGIN PASSWORD 'zaagl_dev';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'coordinator_user') THEN
        CREATE ROLE coordinator_user LOGIN PASSWORD 'zaagl_dev';
    END IF;
END
$$;

ALTER ROLE api_user SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE coordinator_user SET idle_in_transaction_session_timeout = '60s';

-- Spec §6.4: the coordinator reads pg_stat_archiver / pg_ls_archive_statusdir() to confirm reservations
-- against the WAL archive. Granted now so the capability exists once real role wiring happens; the dev
-- coordinator currently runs with Allocator:RequireArchiveConfirmation=false and never calls these.
GRANT pg_monitor TO coordinator_user;
