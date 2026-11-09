# ARC-012 / BE-013: the `worker` service image (background jobs, task tables claimed with FOR UPDATE SKIP LOCKED).
# No HTTP endpoint, no published port, no migrations (the api/migrator image owns them). Build context is the
# repository root (docker-compose.yml's `context: .`), since Zaagl.Worker references the module projects by path.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Zaagl.Worker/Zaagl.Worker.csproj
RUN dotnet publish src/Zaagl.Worker/Zaagl.Worker.csproj -c Release -o /app --no-restore

# aspnet, not runtime: several referenced modules (Files among them) carry a FrameworkReference to
# Microsoft.AspNetCore.App, so the plain .NET runtime image would fail to start the host.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root runtime with a fixed UID/GID 10001 (DEVOPS_PLAN.md §2.2, OPS-003), same as the api and coordinator
# images, so files the worker writes on the shared storage volume keep the same owner as the api's.
RUN groupadd --system --gid 10001 zaagl \
 && useradd --system --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin zaagl
COPY --from=build --chown=10001:10001 /app .
# Storage:Root mount point (docker-compose `storage` volume). Created here with the right owner so a new named
# volume inherits 10001:10001. An existing volume created by another image keeps its owner: chown it once.
RUN mkdir -p /srv/zaagl/storage && chown 10001:10001 /srv/zaagl/storage
USER 10001:10001

# Liveness (ARC-012 §6, src/Zaagl.Worker/LivenessFile.cs): the worker touches this file every
# Worker:LivenessSeconds (15) while PostgreSQL answers. Unhealthy when the file is missing or older than 1 minute
# (4 missed intervals). `find` is part of the Debian base image (findutils is an essential package).
ENV Worker__LivenessFile=/tmp/zaagl-worker-alive
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=3 \
  CMD find /tmp/zaagl-worker-alive -mmin -1 | grep -q . || exit 1

ENTRYPOINT ["dotnet", "Zaagl.Worker.dll"]
