# Spec §3: the `coordinator` service image. Exactly one replica; never on the redirect path, never
# involved in aliases (see src/Zaagl.Coordinator/Program.cs's own header comment).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Zaagl.Coordinator/Zaagl.Coordinator.csproj
RUN dotnet publish src/Zaagl.Coordinator/Zaagl.Coordinator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl: the compose healthcheck below calls /health/live; not in the base image by default.
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Non-root runtime with a fixed UID/GID 10001 (DEVOPS_PLAN.md §2.2, OPS-003), same as the api image.
RUN groupadd --system --gid 10001 zaagl \
 && useradd --system --uid 10001 --gid 10001 --no-create-home --shell /usr/sbin/nologin zaagl
COPY --from=build --chown=10001:10001 /app .
# The coordinator's HWM file + the dev-mode local-filesystem allocator sink both live under /state,
# matching the docker-compose `coordstate` volume mount below. Note: an existing named volume created by
# an older image keeps the old (random) owner: docker-compose.yml's one-shot `volume-init` service corrects that on
# every `up` (it hands /state to 10001:10001 when the owner differs), so a stale volume never leaves the coordinator
# unable to write hwm.json (leases would answer 503).
RUN mkdir -p /state/allocator-sink && chown 10001:10001 /state /state/allocator-sink
USER 10001:10001

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Zaagl.Coordinator.dll"]
