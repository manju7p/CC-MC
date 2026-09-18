# CC-MC — How To Run

> Branch: `windows-application`. This document reflects the **Docker/Neon-based**
> setup, verified in this session by actually building the containers, running
> `dotnet test CCMC.sln` (fresh, this session: 190/190 passing — 155 client +
> 35 cloud), and inspecting the running `cc-mc`/`cc-mc-postgres` containers.
> It supersedes the pre-Docker workflow this file previously described (bare
> `dotnet run` on port 5000, manual local PostgreSQL install) — that workflow
> is gone; Docker Compose is now the only supported way to run the Cloud API.
> **Update (2026-09-15):** §9 now documents the production account bootstrap
> mechanism (done — Neon `production` has real Admin/Manager/Operator
> accounts). Render deployment itself does **not** exist yet — see §10.
> **Update (2026-09-18):** the client is now a single-window shell with a
> Manager/Admin-only Rate Configuration screen — see new §12. Test count is
> now **197/197** (159 client + 38 cloud), re-verified fresh this session
> (superseding the 190/190 figure above); see `STATUS.md`'s dated entries
> for what changed.

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
(verified fresh 2026-09-18): **197/197 passing** — `CCMC.Tests` (client,
159 tests, no external dependency — uses a real temp-file SQLite database)
and `CCMC.Cloud.Api.Tests` (cloud, 38 tests, needs a reachable PostgreSQL at
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
run — by design, `DevelopmentSeeder`'s own demo accounts (§8) never reach
Neon `production`. **Update (2026-09-15):** Neon `production` is no
longer empty — it has real Admin/Manager/Operator accounts created via
the separate, env-var-gated `ProductionBootstrapSeeder` (§9), which is
independent of `DevelopmentSeeder` and equally inert unless explicitly
configured. Migrations still apply automatically on startup, same as
Docker mode.

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

> **Warning — never reuse these exact strings for any real/production
> `Bootstrap__*Password` value.** These are local-dev-only placeholders,
> deliberately documented here in plaintext because `DevelopmentSeeder`
> never runs outside Docker/`Development`. A real production bootstrap
> password (`Bootstrap__AdminPassword`/`Bootstrap__ManagerPassword`/
> `Bootstrap__OperatorPassword` — see §9) must be a unique, strong value
> that does not appear in this table or anywhere else in this repository.

These are placeholder development credentials only, already documented here
and in `STATUS.md`/`README.md` — never used in production, and Neon's
`production` branch has none of these (or any) users.

---

## 9. Production account bootstrap (done, 2026-09-15)

Neon's `production` branch now has real accounts — see
`STATUS.md` "Neon Production Bootstrap (2026-09-15)" for the full writeup.
This runs via `ProductionBootstrapSeeder`
(`src/CCMC.Cloud.Infrastructure/Seed/ProductionBootstrapSeeder.cs`), wired
into `Program.cs` right after migrations, in **any** environment — but it
is a complete no-op unless `Bootstrap:AdminEmail` configuration is present.

To bootstrap a new account/centre (e.g. a second Chilling Centre later):

1. Add `Bootstrap__*` variables to `.env.neon` (never `.env.docker` or a
   committed file) — see the commented block in `.env.example` for the
   full list and format. At minimum: `Bootstrap__AdminEmail`,
   `Bootstrap__AdminFullName`, `Bootstrap__AdminPassword` (12+ characters).
   A Manager/Operator additionally needs `Bootstrap__CentreCode` +
   `Bootstrap__CentreName` (both, together) plus their own
   `Bootstrap__ManagerEmail`/`Bootstrap__OperatorEmail` triplets.
2. `Copy-Item .env.neon .env -Force` then `docker compose -p cc-mc up -d --build`.
3. Check `docker compose -p cc-mc logs api` for `Production bootstrap:
   created <Role> account <email>.` lines (no password is ever logged) —
   or `already exists - left unchanged` if that email already exists
   (existing users' password hashes are never overwritten).
4. Remove the `Bootstrap__*` block from `.env.neon` once done — the
   seeder doesn't need it again, and leaving real passwords in a file
   (even a gitignored one) is unnecessary exposure.
5. Switch back to Docker mode for local dev: `Copy-Item .env.docker .env -Force`
   then `docker compose -p cc-mc up -d --build`.

**Known gap, unchanged by this mechanism:** there is still no in-app
password-change/reset endpoint anywhere in the cloud API. Rotating a
password (bootstrap-issued or otherwise) requires direct database access
using the same `IPasswordHasher` (`PasswordHasherAdapter`, ASP.NET Core
Identity PBKDF2) the app itself uses at runtime — there is no script for
this yet either.

**Security note:** treat every value under a `Bootstrap__*` variable as a
real production credential the moment it's set — same handling as
`Jwt__Secret`/`ConnectionStrings__CcmcDb` (env vars only, gitignored files
only, never printed, never committed). A bootstrap password should be
strong and unique, not a reused dev-seed-style password.

## 10. Production (Render) — BLOCKED (repository access), not yet complete

**Render deployment: BLOCKED / NOT STARTED, pending GitHub repository
access from the repository owner/CEO.** The GitHub repository is owned/
controlled by the CEO, and the current operator does not have the access
needed to connect the private repository to Render. This is an
access/permissions blocker, not a technical one — the Docker image and
Neon backend are both already deployment-ready:

- `Dockerfile`/image builds and runs correctly locally against Neon (§6,
  verified) — that is the exact artifact a future Render deployment would
  publish.
- Neon `production` now has real accounts and one Chilling Centre (§9,
  done 2026-09-15).

What's still missing before Render can go live, once repository access is
obtained:

1. Connect the GitHub repository to Render.
2. Deploy the existing `Dockerfile`/image as a Render Web Service.
3. Configure Render's own environment variables
   (`ConnectionStrings__CcmcDb`, `Jwt__Secret`, etc.) pointing at Neon, via
   Render's dashboard/CLI — never committed to this repo.
4. Verify Render → Neon connectivity for real (`/health`, `/health/db`
   against the Render URL).
5. Point the WPF client's `CloudApi:BaseUrl` at the Render HTTPS URL
   instead of `http://localhost:8081/` for a real deployed client. **Not
   done in this pass, and not to be done without an explicit
   instruction** — see §11 for what the WPF client currently points at.
6. Real-world end-to-end verification, including a real first login
   against Neon's bootstrapped accounts.

Do not invent instructions beyond the above until they are actually
implemented, and do not attempt to work around the access blocker (e.g.
by seeking elevated access, forking, or making the repo public) without
being explicitly asked — see `STATUS.md` "Checkpoint" → BLOCKED/NEXT and
`progress.md` §20 "Checkpoint (2026-09-15)" for the full detail.

## 11. Current WPF API configuration — API host vs. database host

Two different things change independently, and this repo's docs are
careful to keep them distinct:

- **API host** — where the WPF client's HTTP requests go
  (`src/CCMC.Desktop/appsettings.json`'s `CloudApi:BaseUrl`). Currently
  `http://localhost:8081/`, **unchanged by this documentation pass**. This
  is the same value in both Docker-local mode and Neon-backed mode (§5/§6)
  — the client always talks to the locally running `cc-mc` container on
  `localhost:8081`; only what's *behind* that container changes. It will
  change to the Render HTTPS URL only once Render is actually deployed
  (§10) — not before, and not as part of this pass.
- **Database host** — where `cc-mc` itself connects
  (`ConnectionStrings__CcmcDb` inside `.env`/`.env.docker`/`.env.neon`).
  This is what actually differs between Docker mode (`cc-mc-postgres`) and
  Neon mode (Neon's hostname) — the WPF client has no visibility into this
  at all and never connects to a database host directly (see `CLAUDE.md`
  "Constraints").

Verified directly in this session: `appsettings.json`'s `CloudApi:BaseUrl`
is `http://localhost:8081/`; the currently-running stack is in Docker mode
(`ASPNETCORE_ENVIRONMENT=Development`, `cc-mc` talking to
`cc-mc-postgres`) — Neon mode was used only transiently, for the §9
bootstrap, then switched back to Docker mode for local development.

## 12. Using the Windows application — navigation and Rate Configuration

(Added 2026-09-18, describing the current single-window shell. See
`STATUS.md`'s "Single-Window Shell & Navigation Redesign (2026-09-18)" for
why/how this replaced the previous per-window design, and `rateconfig.md`
for the full Rate Configuration explanation aimed at a Manager.)

After building/running the client (§3/§4) with the cloud API reachable
(§5 or §6), launching `CCMC.Desktop.exe` opens `LoginWindow`. Signing in
successfully opens **one** main application window — there is no longer a
separate window per feature:

```
LoginWindow (email + password)
    ↓ successful sign-in
MainWindow — single window, left sidebar + right content area
    ├── Dashboard          (default content on open)
    ├── Milk Reception
    ├── Reception History  (Today/Past Week/Past Month/Past Year/Total
    │                        filter + status filter + search, combined)
    ├── Sources             ← Manager/Admin only, hidden for Operator
    ├── Vehicles            ← Manager/Admin only, hidden for Operator
    ├── Rate Configuration  ← Manager/Admin only, hidden for Operator
    ├── Synchronization
    └── Settings            (General / Device Configuration / Device
                              Status as three sections on one screen —
                              no separate Device Configuration/Status
                              sidebar entries exist anymore)
```

Clicking a sidebar item replaces the content on the right; it never opens
a new window. The signed-in user's role determines which items are
visible (checked by role name, not by the underlying view permission —
see `CLAUDE.md`'s "Rate calculation" bullet for why) — an Operator account
never sees Sources, Vehicles, or Rate Configuration in this sidebar at
all, regardless of what the cloud API itself would permit them to *view*.

**Reaching Rate Configuration as a Manager**, using the already-documented
development credentials from §8 above (Docker mode only — never use these
for a real deployment):

```
Sign in as manager1@ccmc.local / Manager@12345 (Manager, BLR-CC-01)
    ↓
Left sidebar → "Rate Configuration"
    ↓
Select the chilling centre (only centres this account has access to
are listed), select a calculation mode, fill in that mode's parameters
    ↓
Click "Save Configuration"
    ↓
Perform (or re-perform) a Milk Reception at that centre — the "RATE &
AMOUNT" card now reflects the saved configuration
```

Full step-by-step detail, the exact meaning of each field, the exact
formulas, and troubleshooting are in `rateconfig.md` — this section only
covers how to reach the screen, not what each field means.

**Known open issue (documented, not hidden):** a textbox left-padding
problem on the Login screen and on the History/Sources/Vehicles search
boxes was structurally reworked on 2026-09-18, but the developer's own
commit message for that exact change records it as still visually present
after manual testing (`dfa9ad9 "Rate config added -- text box bug still an
issue"`). This does not affect Rate Configuration's own input fields
(Value 1/Value 2/TS Rate use a different, unaffected textbox style — see
`rateconfig.md` §13) and does not affect any calculation, persistence, or
sync behavior — it is a cosmetic issue on specific screens only.
