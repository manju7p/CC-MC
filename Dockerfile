# CCMC.Cloud.Api - production container image.
#
# Build from the REPOSITORY ROOT (ProjectReference paths in the .csproj files
# are relative, e.g. ..\CCMC.Cloud.Domain\..., so the build context must
# include every referenced project, not just CCMC.Cloud.Api's own folder):
#
#   docker build -t cc-mc-api .
#
# Dependency graph actually used (verified against the .csproj files, not
# assumed): CCMC.Cloud.Api -> CCMC.Cloud.Domain, CCMC.Cloud.Application,
# CCMC.Cloud.Infrastructure, CCMC.Contracts. CCMC.Cloud.Application ->
# CCMC.Cloud.Domain, CCMC.Contracts, CCMC.Cloud.Infrastructure.
# CCMC.Cloud.Infrastructure -> CCMC.Cloud.Domain, CCMC.Contracts. The Windows
# client projects (CCMC.Domain/Application/Infrastructure/Desktop) and
# tests/ are NOT referenced by this dependency graph and are excluded via
# .dockerignore.
#
# Configuration (ConnectionStrings__CcmcDb, Jwt__Secret, ASPNETCORE_ENVIRONMENT)
# is supplied ONLY via environment variables at `docker run` / hosting-platform
# time - see README.md "Configuration" / "Production Security Notes". This
# image never contains a real secret; appsettings.Development.json (dev-only
# placeholder values) is excluded from the build context entirely via
# .dockerignore, so it cannot end up in this image in any environment.

# ---- Build stage ------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the .csproj files first so `dotnet restore` is cached by Docker
# whenever source changes but dependencies don't.
COPY src/CCMC.Contracts/CCMC.Contracts.csproj src/CCMC.Contracts/
COPY src/CCMC.Cloud.Domain/CCMC.Cloud.Domain.csproj src/CCMC.Cloud.Domain/
COPY src/CCMC.Cloud.Application/CCMC.Cloud.Application.csproj src/CCMC.Cloud.Application/
COPY src/CCMC.Cloud.Infrastructure/CCMC.Cloud.Infrastructure.csproj src/CCMC.Cloud.Infrastructure/
COPY src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj src/CCMC.Cloud.Api/

RUN dotnet restore src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj

# Now copy the actual source for exactly these five projects (nothing else is
# needed, and nothing else is present - see .dockerignore).
COPY src/CCMC.Contracts/ src/CCMC.Contracts/
COPY src/CCMC.Cloud.Domain/ src/CCMC.Cloud.Domain/
COPY src/CCMC.Cloud.Application/ src/CCMC.Cloud.Application/
COPY src/CCMC.Cloud.Infrastructure/ src/CCMC.Cloud.Infrastructure/
COPY src/CCMC.Cloud.Api/ src/CCMC.Cloud.Api/

RUN dotnet publish src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---- Runtime stage ------------------------------------------------------------
# ASP.NET runtime only (no SDK) - smaller image, no build tooling shipped.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Default to Production so the Development-only seeder/Swagger never run
# unless a deployer explicitly overrides this - see Program.cs. Every other
# setting (ConnectionStrings__CcmcDb, Jwt__Secret) MUST be supplied at
# `docker run` time; there is no fallback value baked in here.
ENV ASPNETCORE_ENVIRONMENT=Production

# The official ASP.NET Core 8 runtime image already defaults
# ASPNETCORE_HTTP_PORTS=8080 (binds 0.0.0.0:8080) - EXPOSE is documentation
# only (Docker does not enforce it), matching that default. Program.cs
# additionally honors a platform-injected PORT env var (e.g. Render) if set,
# overriding this default without requiring an image change.
EXPOSE 8080

ENTRYPOINT ["dotnet", "CCMC.Cloud.Api.dll"]
