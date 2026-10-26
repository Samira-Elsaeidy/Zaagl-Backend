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

RUN groupadd -r zaagl && useradd -r -g zaagl zaagl
COPY --from=build /app .
# The coordinator's HWM file + the dev-mode local-filesystem allocator sink both live under /state,
# matching the docker-compose `coordstate` volume mount below.
RUN mkdir -p /state && chown -R zaagl:zaagl /app /state
USER zaagl

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Zaagl.Coordinator.dll"]
