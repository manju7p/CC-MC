# CC-MC — How To Run

> Branch: `windows-application`. This document reflects the **Docker/Neon-based**
> setup, verified in this session by actually building the containers, running
> `dotnet test CCMC.sln` (fresh, this session: 190/190 passing — 155 client +
> 35 cloud), and inspecting the running `cc-mc`/`cc-mc-postgres` containers.
> It supersedes the pre-Docker workflow this file previously described (bare
> `dotnet run` on port 5000, manual local PostgreSQL install) — that workflow
> is gone; Docker Compose is now the only supported way to run the Cloud API.
> Render deployment does **not** exist yet — see §9.

---

## 1. Architecture

```
WPF Desktop (CCMC.Desktop, .NET 8)
    |  HTTP + Bearer JWT   →  CloudApi:BaseUrl = http://localhost:8081/
    |                          (identical in Docker mode and Neon mode)
    v
cc-mc  (Docker container — CCMC.Cloud.Api, ASP.NET Core 8)
    |  Npgsql / EF Core
    v
EITHER  cc-mc-postgres  (Docker container, local Postgres 16)   [Docker mode]
   OR   Neon PostgreSQL  (project fancy-cherry-25725711, branch production)  [Neon mode]
```

The WPF client **never** connects directly to PostgreSQL/Neon — see
`context.md` "Constraints" and `CLAUDE.md` "Security baseline". Only the
database target changes between modes; the API container, its port mapping,
and all application source code are identical.

---

## 2. Prerequisites

| Tool | Needed for |
|---|---|
| .NET 8 SDK | Building/testing all projects, running the WPF client |
| Docker Desktop (or Docker Engine) with Compose v2 | Running the Cloud API in either mode |
| Neon CLI (`npm install -g neon`, or `npx neon`) | Only if you need to manage the Neon project/branch directly — not required to just run in Neon mode |

No local PostgreSQL install is required for either mode — Docker mode runs
its own Postgres container, and Neon mode uses the existing hosted database.

---

## 3. Build

From the repository root:

```powershell
dotnet build CCMC.sln
```

## 4. Test

```powershell
dotnet test CCMC.sln
```

This runs both test projects and prints one combined result. Current state
(verified fresh this session): **190/190 passing** — `CCMC.Tests` (client,
155 tests, no external dependency — uses a real temp-file SQLite database)
and `CCMC.Cloud.Api.Tests` (cloud, 35 tests, needs a reachable PostgreSQL at
`Host=localhost;Port=5432;Database=ccmc_cloud_test;Username=postgres;Password=postgres`
— a throwaway Postgres container on port 5432 is enough; it does not need to
be the same `cc-mc-postgres` container, and nothing in this project ever
targets `ccmc_cloud_dev`).

To run just the cloud suite against a disposable Postgres:

```powershell
docker run -d --name ccmc-test-pg -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=ccmc_cloud_test -p 5432:5432 postgres:16
dotnet test tests/CCMC.Cloud.Api.Tests/CCMC.Cloud.Api.Tests.csproj
docker rm -f ccmc-test-pg
```

---

## 5. Docker mode (local Postgres)

```powershell
Copy-Item .env.docker .env -Force
docker compose -p cc-mc up -d --build
```

Check status:

```powershell
docker compose -p cc-mc ps
```

Expected containers: `cc-mc` (API) and `cc-mc-postgres` (Postgres 16,
`profiles: [docker]`, only created in this mode). Health:

```powershell
curl http://localhost:8081/health       # liveness — always 200 if the process is up
curl http://localhost:8081/health/db    # readiness — proves actual PostgreSQL connectivity
```

The API is reachable at `http://localhost:8081/`. `ASPNETCORE_ENVIRONMENT`
defaults to `Development` in this mode, so `DevelopmentSeeder` runs
automatically on first startup (see §8 for the seeded accounts) and EF Core
migrations are applied automatically on every startup, in every environment
(`db.Database.Migrate()` in `Program.cs` — unconditional, not gated by
environment; only the *seed* step is Development-only).

Now start the WPF client (`src/CCMC.Desktop`) normally, e.g. `dotnet run
--project src/CCMC.Desktop` or via Visual Studio/Rider. `appsettings.json`'s
`CloudApi:BaseUrl` is already `http://localhost:8081/` and does not need to
change between modes.

---

## 6. Neon mode (hosted Postgres)

```powershell
Copy-Item .env.neon .env -Force
docker compose -p cc-mc up -d --build
```

What changes: `.env.neon` sets `ASPNETCORE_ENVIRONMENT=Production` and a
Neon connection string, and — critically — does **not** set
`COMPOSE_PROFILES=docker`. Because the `postgres` service in `compose.yaml`
is gated behind `profiles: [docker]`, Compose does not even create a
`cc-mc-postgres` container in this mode (not merely "leave it stopped" —
it's absent from `docker compose -p cc-mc ps` entirely). Only the `cc-mc`
API container starts, and it connects straight to Neon over TLS.

`ASPNETCORE_ENVIRONMENT=Production` means `DevelopmentSeeder` does **not**
run — Neon's `production` branch currently has **zero users** by design (see
§8). Migrations still apply automatically on startup, same as Docker mode.

The WPF client's configuration is unchanged — `CloudApi:BaseUrl` still points
at `http://localhost:8081/`, because the `cc-mc` container's host port
mapping (`${API_HOST_PORT:-8081}:8080`) is identical in both modes.

---

## 7. Switching back / stopping / logs

Switch back to Docker mode:

```powershell
Copy-Item .env.docker .env -Force
docker compose -p cc-mc up -d --build
```

Stop everything:

```powershell
docker compose -p cc-mc down
```

(Add `-v` only if you intentionally want to delete the `cc-mc-postgres-data`
volume — this destroys all local Docker-mode data. Never do this against
Neon mode; Neon's data lives outside Docker entirely and `down -v` cannot
touch it.)

Logs:

```powershell
docker compose -p cc-mc logs -f api
docker compose -p cc-mc logs -f postgres   # Docker mode only — service doesn't exist in Neon mode
```

---

## 8. Database migrations & development users

Migrations are EF Core migrations (`src/CCMC.Cloud.Infrastructure/Persistence/Migrations/`)
applied automatically at container startup via `db.Database.Migrate()` in
`Program.cs` — there is no manual `dotnet ef database update` step for either
mode today. Current migrations, in order: `InitialCreate`,
`AddMilkAnalyserFields`, `AddRateFormulaCalculation`.

**Development-only seeded users** (Docker mode / `ASPNETCORE_ENVIRONMENT=Development`
only — created by `DevelopmentSeeder`, never in Production/Neon):

| Email | Password | Role | Centre access |
|---|---|---|---|
| `admin@ccmc.local` | `Admin@12345` | Admin | All centres |
| `manager1@ccmc.local` | `Manager@12345` | Manager | Bangalore (`BLR-CC-01`) |
| `operator1@ccmc.local` | `Operator@12345` | Operator | Bangalore (`BLR-CC-01`) |
| `operator2@ccmc.local` | `Operator@12345` | Operator | Mysore (`MYS-CC-01`) |

These are placeholder development credentials only, already documented here
and in `STATUS.md`/`README.md` — never used in production, and Neon's
`production` branch has none of these (or any) users.

---

## 9. Production (Render) — NOT yet complete

Render deployment has **not** been performed. What exists today: the same
`Dockerfile`/image builds and runs correctly locally against Neon
(§6, verified) — that is the artifact a future Render deployment would
publish. What's still missing before Render can go live:

- **Production user bootstrap.** Neon's `production` branch has zero users
  and `DevelopmentSeeder` correctly never runs outside Development — there is
  currently no mechanism (script, one-time endpoint, or otherwise) to create
  the first Admin user in Production. This is a genuine open prerequisite,
  not yet solved.
- Actual Render service creation/deployment and its own environment variable
  configuration (`ConnectionStrings__CcmcDb`, `Jwt__Secret`, etc., set via
  Render's dashboard/CLI, not committed anywhere).
- Pointing the WPF client's `CloudApi:BaseUrl` at the Render HTTPS URL
  instead of `http://localhost:8081/` for a real deployed client.

Do not invent instructions for any of the above until they are actually
implemented — see `STATUS.md` "Remaining Gaps" / `progress.md` "Next Step".
