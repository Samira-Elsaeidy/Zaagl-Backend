# Spec §3: the `api` service image. Also serves as the `migrator` (entrypoint arg "migrate") and is the
# base for a future `ops` entrypoint (not built - no Zaagl.Ops project exists yet in this build, see
# docs/TRACEABILITY.md). Build context is the repository root (docker-compose.yml's `context: .`), since
# this modular monolith's Zaagl.Api project references every module project by path.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Zaagl.Api/Zaagl.Api.csproj
RUN dotnet publish src/Zaagl.Api/Zaagl.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl: the compose healthcheck below calls /health/live; not in the base image by default.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Non-root runtime (spec §3) with a fixed UID/GID 10001 so files on the storage volume keep stable
# ownership across image rebuilds (DEVOPS_PLAN.md §2.2, OPS-003). The numeric USER lets an orchestrator
# verify non-root without resolving names.
RUN groupadd --system --gid 10001 zaagl \
 && useradd --system --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin zaagl
COPY --from=build --chown=10001:10001 /app .
# Storage:Root mount point (docker-compose `storage` volume, shared with the worker). Created here with the right
# owner so a new named volume inherits 10001:10001. An existing volume created by another image keeps its owner:
# chown it once.
RUN mkdir -p /srv/zaagl/storage && chown 10001:10001 /srv/zaagl/storage
USER 10001:10001

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Zaagl.Api.dll"]
