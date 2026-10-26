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

# Non-root runtime (spec §3).
RUN groupadd -r zaagl && useradd -r -g zaagl zaagl
COPY --from=build /app .
RUN chown -R zaagl:zaagl /app
USER zaagl

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Zaagl.Api.dll"]
