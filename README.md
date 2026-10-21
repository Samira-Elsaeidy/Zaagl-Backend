# Zaagl-Backend

ASP.NET Core 10 modular monolith + PostgreSQL 18 for **Zaagl** (zaa.gl). One deployable API, one worker, one database with a schema per module.

## Layout
- `src/Zaagl.Api` - Minimal API host; one `Endpoints/<Module>Endpoints.cs` per module.
- `src/Zaagl.Worker` - Worker Services (Wolverine handlers: analytics aggregation, expiry, media, scanning).
- `src/Zaagl.SharedKernel` - `IFileStorage`, Sqids short-code generator, shared types.
- `src/Modules/<Name>` - Domain / Application / Infrastructure / Contracts. Other modules may only call `Contracts`.
- `tests/` - Links, Snippets, Files test projects (add more per module as they grow).

Modules: Identity, Organizations, Links, Resolution, Access, Analytics, Resources, Snippets, Files, Media, DeveloperTools, Collaboration, AI, Billing, Notifications, Administration.

## Notes
- Remote is still `Zagel-Backend`; rename the GitHub repo when ready.
- Files live on local disk behind `IFileStorage`, served via Nginx `X-Accel-Redirect` after authorization.
- Strict counters (one-time access, click limits) stay authoritative in PostgreSQL, not Valkey.
