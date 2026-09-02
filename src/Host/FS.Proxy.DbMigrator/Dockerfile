# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/ ./src/

# Project-level restore — the .dockerignore at repo root excludes src/Tests/
# which the solution file references, so `dotnet restore src/FS.Proxy.slnx`
# would fail inside the build context. Project-level restore pulls the same
# transitive graph for our purposes.
RUN dotnet restore src/Host/FS.Proxy.DbMigrator/FS.Proxy.DbMigrator.csproj
RUN dotnet publish src/Host/FS.Proxy.DbMigrator/FS.Proxy.DbMigrator.csproj \
    -c Release \
    --no-restore \
    -o /publish

FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /publish .

USER $APP_UID
# Migrator reads DOTNET_ENVIRONMENT to pick appsettings.<env>.json overrides.
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "FS.Proxy.DbMigrator.dll"]
# Default command: apply migrations. Compose overrides with `command: ["apply"]`
# explicitly so it's grep-able in the compose file.
CMD ["apply"]
