# CC-MC — How To Run

> Branch: `windows-application`. Every command and result in this document was
> actually executed and observed in this session (build, database, API
> startup, `curl`/`Invoke-RestMethod` calls, and the real WPF application
> driven end-to-end for admin/manager/operator1/operator2 login) — nothing
> here is theoretical. Where something could not be verified, that is stated
> explicitly instead of assumed.

---

## 1. Architecture

```
PostgreSQL  (ccmc_cloud_dev)
    |  Npgsql / EF Core
    v
CCMC Cloud API   (ASP.NET Core 8, project src/CCMC.Cloud.Api)
    |  HTTP  (POST /auth/login, GET /centres, /sources, /vehicles,
    |         /quality-rules, POST /reception, GET /health, ...)
    v
CCMC Windows WPF Application   (project src/CCMC.Desktop, .NET 8)
    |
    v
Local SQLite (%LocalAppData%\CCMC\ccmc.db)  +  RS232 devices (weighing scale, analyser)
```

**The Windows application never connects directly to PostgreSQL.** There is
no `Npgsql` reference anywhere under `src/CCMC.Desktop`, `CCMC.Domain`,
`CCMC.Application`, or `CCMC.Infrastructure`. All cloud communication goes
through the Cloud API's HTTP contract (`ICloudApiClient` /
`HttpCloudApiClient`).

---

## 2. Prerequisites (as actually present on this machine)

| Tool | Verified version | Verify with |
|---|---|---|
| .NET 8 SDK | `8.0.424` | `dotnet --version` |
| PostgreSQL | 16, running as Windows service `postgresql-x64-16` | `Get-Service postgresql-x64-16` |
| `psql` CLI | ships with PostgreSQL 16 at `C:\Program Files\PostgreSQL\16\bin\psql.exe` | `psql --version` |
| `dotnet-ef` global tool | `8.0.11` (matches `Microsoft.EntityFrameworkCore.Design` in both `.csproj` files) | `dotnet ef --version` |
| Windows | required — `CCMC.Desktop` targets `net8.0-windows`, uses WPF + `System.IO.Ports` + DPAPI | — |

`dotnet.exe` is not on `PATH` in a Bash shell in this environment — use the
full path (`C:\Program Files\dotnet\dotnet.exe`) or prepend it to `PATH`, e.g.
`export PATH="/c/Program Files/dotnet:$PATH"`. Not an issue in PowerShell.

---

## 3. Clone / Checkout

```powershell
git clone <repository-url> CC-MC
cd CC-MC
git checkout windows-application
git branch --show-current   # should print: windows-application
```

Do **not** check out, merge, or modify `pranav-dev` — the old Node/NestJS/React
implementation, unrelated to this workflow.

---

## 4. Restore Dependencies

```powershell
dotnet restore CCMC.sln
```

---

# PART A — DATABASE

## 5. Start PostgreSQL

```powershell
Get-Service -Name postgresql*
```

If not `Running`:

```powershell
Start-Service -Name postgresql-x64-16
```

(Service name may differ per install — use the name reported above.)

## 6. Create the Databases

The Cloud API uses **`ccmc_cloud_dev`**; the integration test suite uses a
separate **`ccmc_cloud_test`**, so tests never touch dev data.

```powershell
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_dev;"
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_test;"
```

If either already exists, `CREATE DATABASE` fails with `database "..." already
exists` — that's fine, no action needed. (On this machine both already
existed with the schema fully migrated and seeded — see §10.)

## 7. Database Connection Configuration

`src/CCMC.Cloud.Api/appsettings.Development.json` (committed, dev-only
placeholder values, used automatically when `ASPNETCORE_ENVIRONMENT=Development`):

```json
{
  "ConnectionStrings": {
    "CcmcDb": "Host=localhost;Port=5432;Database=ccmc_cloud_dev;Username=postgres;Password=postgres"
  },
  "Jwt": {
    "Secret": "dev-only-local-signing-secret-not-a-real-secret-CHANGE-FOR-ANY-SHARED-ENVIRONMENT-0123456789",
    "Issuer": "ccmc-cloud-api",
    "Audience": "ccmc-windows-client",
    "ExpiryMinutes": 480
  }
}
```

This is the exact connection string that was verified working (`psql -U
postgres -h localhost -p 5432 -c "SELECT 1;"` succeeds with password
`postgres`, no prompt issue on this machine). If your local PostgreSQL
superuser credentials differ, edit this file locally — never commit real
credentials.

`Program.cs` throws `InvalidOperationException` at startup if
`ConnectionStrings:CcmcDb` or `Jwt:Secret` is missing — there is no silent
fallback, and a non-Development environment must supply both via environment
variables (`ConnectionStrings__CcmcDb`, `Jwt__Secret`), never a committed file.

## 8. EF Core CLI

Already installed and verified on this machine (`dotnet-ef` `8.0.11`,
matching the `Microsoft.EntityFrameworkCore.Design` package version pinned in
`CCMC.Cloud.Api.csproj`/`CCMC.Cloud.Infrastructure.csproj`):

```powershell
dotnet tool install --global dotnet-ef --version 8.0.11
# or, if already installed at a different version:
dotnet tool update --global dotnet-ef --version 8.0.11
dotnet ef --version
```

## 9. Apply Migrations

- **Migrations project**: `src/CCMC.Cloud.Infrastructure` (one migration:
  `20260905085204_InitialCreate.cs`).
- **Startup project**: `src/CCMC.Cloud.Api`.

```powershell
dotnet ef database update `
  --project src/CCMC.Cloud.Infrastructure/CCMC.Cloud.Infrastructure.csproj `
  --startup-project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj
```

**This step is optional in practice**: `Program.cs` calls
`db.Database.Migrate()` unconditionally at startup, in every environment —
verified directly in this session's own API startup log:

```
info: Program[0]
      Applying database migrations...
info: Microsoft.EntityFrameworkCore.Migrations[20405]
      No migrations were applied. The database is already up to date.
info: Program[0]
      Database migrations up to date.
```

So simply starting the API (§13) applies any pending migration automatically.

## 10. Verify the Database

Actually run in this session, against `ccmc_cloud_dev`:

```powershell
psql -U postgres -h localhost -d ccmc_cloud_dev -c "\dt"
```

Confirmed present: `__EFMigrationsHistory`, `users`, `roles`, `permissions`,
`role_permissions`, `user_roles`, `user_centre_assignments`,
`chilling_centres`, `sources`, `vehicles`, `quality_rules`,
`milk_reception_transactions`, `transaction_overrides`, `audit_logs`.

```powershell
psql -U postgres -h localhost -d ccmc_cloud_dev -c "SELECT * FROM ""__EFMigrationsHistory"";"
```

Confirmed exactly one row: `20260905085204_InitialCreate`.

```powershell
psql -U postgres -h localhost -d ccmc_cloud_dev -c "SELECT email, ""FullName"" FROM users;"
```

Confirmed exactly the four accounts listed in §15.

---

# PART B — CLOUD API

## 11. Build

```powershell
dotnet build CCMC.sln
```

Actually run in this session: **`Build succeeded. 0 Warning(s). 0 Error(s).`**
(11 projects: 5 Windows-client, 5 cloud, matching solution structure, plus
both test projects.)

## 12. THE BUG THAT WAS FOUND AND FIXED — read this before starting the API

**Root cause of "I cannot reliably run the Cloud API / log in as admin or
manager":** a **port mismatch** between two committed configuration files.

- `src/CCMC.Desktop/appsettings.json` (the Windows client's own config) is
  committed with `CloudApi:BaseUrl = "http://localhost:5000/"`.
- `src/CCMC.Cloud.Api/Properties/launchSettings.json` (before this fix) had
  its default `http` profile listening on `http://localhost:5179`, and its
  `https` profile on `https://localhost:7123;http://localhost:5179` — **neither
  matches port 5000.**

A plain `dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj` (the
first thing anyone would naturally type) uses the **first** profile in
`launchSettings.json`, so the API came up on port **5179**, while the Windows
app only ever tried port **5000**. This was reproduced directly in this
session:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5179
```
```
curl http://localhost:5000/health --max-time 3
→ connection failed: nothing listening on 5000
```

This exact failure was also found already recorded in this machine's own
real application log from an earlier session
(`%LocalAppData%\CCMC\logs\ccmc-20260910.log`):

```
HTTP request failed ... HttpRequestException: No connection could be made
because the target machine actively refused it. (localhost:5000)
...
Cloud unreachable for login - attempting offline credential verification for admin@ccmc.local.
No cached offline credential for admin@ccmc.local.
Offline login failed for admin@ccmc.local (no cached credential, or password did not match).
...
Login succeeded (OFFLINE) for operator1@ccmc.local (user 3).
```

This is exactly the reported symptom: **admin/manager can never log in**
(they have no prior successful online login on this machine, so there is no
offline credential cache to fall back to), while **operator1 appears to
"work"** only because it happened to have a cached offline credential from a
previous session — silently masking the fact that the Cloud API was never
actually reachable.

**Fix applied** (the only source change in this pass, and the whole reason
the system is reliably runnable now): `src/CCMC.Cloud.Api/Properties/launchSettings.json`
was edited so the default `http` profile listens on `http://localhost:5000`
(and the `https` profile's plain-HTTP fallback also on `5000`), matching the
Windows client's own committed `BaseUrl` exactly:

```diff
- "applicationUrl": "http://localhost:5179",
+ "applicationUrl": "http://localhost:5000",
...
- "applicationUrl": "https://localhost:7123;http://localhost:5179",
+ "applicationUrl": "https://localhost:7123;http://localhost:5000",
```

No other file was changed. `CCMC.Desktop/appsettings.json` was left exactly
as committed (`http://localhost:5000/`) — it was already correct; the launch
profile was wrong, not the client.

**Verified after the fix:** a bare `dotnet run` (no `--urls` flag, no
profile argument) now listens on `http://localhost:5000`:

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
```

## 13. Start the Cloud API

From the repository root, in its own terminal window (leave it running):

```powershell
dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj
```

- Listens on **`http://localhost:5000`** (plain HTTP) — verified.
- `ASPNETCORE_ENVIRONMENT=Development` is set automatically by the `http`
  launch profile — this is what gates Swagger and the development seeder.
- On every startup it applies any pending EF Core migrations, then (in
  Development only) runs the idempotent seed described in §15.

## 14. Verify the API

All of the following were actually run against the running instance in this
session and returned exactly what's shown:

```powershell
Invoke-RestMethod -Uri "http://localhost:5000/health"
# -> Healthy   (liveness only, zero checks actually run, no DB dependency)

Invoke-RestMethod -Uri "http://localhost:5000/health/db"
# -> Healthy   (runs the real PostgreSQL connectivity check)
```

Swagger UI (Development only):

```
http://localhost:5000/swagger
```

Verified: `GET /swagger` → `301` redirect → `GET /swagger/index.html` → `200`.
Paste only the `accessToken` value from `POST /auth/login` into the
"Authorize" button — Swagger adds the `Bearer ` prefix itself.

---

## 15. Development Login Credentials

Verified directly from `src/CCMC.Cloud.Infrastructure/Seed/DevelopmentSeeder.cs`
**and** by actually calling `POST /auth/login` for every one of these accounts
against the running API in this session (each returned `200` with a real JWT
and the role/centre data shown):

| Email | Password | Role | Centre access |
|---|---|---|---|
| `admin@ccmc.local` | `Admin@12345` | Admin | All centres |
| `manager1@ccmc.local` | `Manager@12345` | Manager | Bangalore (`BLR-CC-01`) only |
| `operator1@ccmc.local` | `Operator@12345` | Operator | Bangalore (`BLR-CC-01`) only |
| `operator2@ccmc.local` | `Operator@12345` | Operator | Mysore (`MYS-CC-01`) only |

**DEVELOPMENT ONLY.** The seeder runs only when
`ASPNETCORE_ENVIRONMENT=Development` (true for the default `dotnet run`
profile per §13); nothing in `CCMC.Desktop` hardcodes or assumes these
values. A wrong password against a real, reachable API is rejected with
`401 {"message":"Invalid credentials"}` — verified directly.

Also seeded and verified present: global quality rules (Fat 3.0–6.0, SNF
8.0–10.0, Temperature 0–10 °C), one source and one vehicle per centre.

---

# PART C — WINDOWS APPLICATION

## 16. Configure the API URL

`src/CCMC.Desktop/appsettings.json` — committed, unchanged, already correct:

```json
{ "CloudApi": { "BaseUrl": "http://localhost:5000/" } }
```

Must end with a trailing slash. Loaded once at startup (not hot-reloaded) —
change it and restart the app if you ever run the API on a different port.

## 17. Build

```powershell
dotnet build src/CCMC.Desktop/CCMC.Desktop.csproj
```

Verified: `Build succeeded. 0 Warning(s). 0 Error(s).`

## 18. Run

```powershell
dotnet run --project src/CCMC.Desktop/CCMC.Desktop.csproj
```

or open `CCMC.sln` in Visual Studio, set `CCMC.Desktop` as the startup
project, and press F5. Framework-dependent publish (target machine needs the
.NET 8 Desktop Runtime, not just the SDK):

```powershell
dotnet publish src/CCMC.Desktop/CCMC.Desktop.csproj -c Release -r win-x64 --self-contained false -o publish/CCMC.Desktop
```

## 19. Login — verified end-to-end in this session

With the Cloud API running (§13), the built app was actually launched and
driven through its real UI (not just inspected as source) for all four
accounts:

1. **admin@ccmc.local** → Sign In → Dashboard. Log line:
   `Login succeeded (online) for admin@ccmc.local (user 1)`, followed by
   successful `GET /centres`, `/sources`, `/vehicles`, `/quality-rules` (all
   `200`) — master data sync confirmed.
2. **manager1@ccmc.local** → logged out of admin, logged in → Dashboard.
   Log line: `Login succeeded (online) for manager1@ccmc.local (user 2)`,
   same master-data sync confirmed.
3. **operator1@ccmc.local** → logged out of manager, logged in → Dashboard.
   Log line: **`Login succeeded (online) for operator1@ccmc.local (user 3)`**
   — this specifically proves operator1 now authenticates through the live
   Cloud API, not merely through a locally cached offline credential (see
   §20 for the explicit before/after proof).
4. **operator2@ccmc.local** → logged in → Dashboard, same online path.

Login flow, confirmed from the actual code path exercised: `LoginWindow` →
`AuthenticationService.LoginAsync` → `POST http://localhost:5000/auth/login`
→ on success, access token cached in-memory, an offline-credential verifier
refreshed (DPAPI + Argon2id, §20), master data pulled, `MainWindow` (titled
"CCMC - Dashboard") opens.

---

## 20. Offline Login Behavior — verified, not redesigned

The offline credential mechanism (`OfflineCredentialStore`,
`AuthenticationService`) was inspected and its actual runtime behavior
confirmed by test, not just read:

**What it is:** a per-Windows-user, DPAPI-protected, Argon2id-hashed
credential verifier, written *only* as a side effect of a successful
*online* login, and consulted *only* when the Cloud API is genuinely
unreachable. The plaintext password and the cloud access token are never
stored.

**Verified in this session** by stopping the running Cloud API process and
attempting two logins:

| Account | Cloud reachable? | Cached offline credential? | Result |
|---|---|---|---|
| `operator2@ccmc.local` | No (API stopped) | No (never logged in on this machine before) | **Correctly rejected**: log shows `No cached offline credential for operator2@ccmc.local` → `Offline login failed`. UI stayed on Sign In. |
| `operator1@ccmc.local` | No (API stopped) | Yes (cached moments earlier by its own successful online login in §19) | **Correctly succeeded offline**: log shows `Login succeeded (OFFLINE) for operator1@ccmc.local (user 3)`. UI reached Dashboard. |

This confirms the mechanism works exactly as designed: an online rejection is
never second-guessed, a genuinely unreachable cloud falls back only for an
account with a real prior successful online login on this machine, and an
account with no such history fails cleanly with a specific log reason. The
Cloud API was restarted afterward and re-verified reachable on port 5000
before finishing this pass.

**Known, harmless cosmetic inaccuracy, not fixed (out of scope for this
pass):** `LoginWindow.xaml`'s static hint text reads *"Offline sign-in is not
yet supported"* — this is stale UI copy; the feature is implemented and was
just verified working above. Not changed, since fixing wording was not
required to make login work and this pass's instructions were to touch only
what's required for that.

---

# PART D — TESTING

## 21. Run Tests

Both actually executed in this session, with the Cloud API's own
`WebApplicationFactory`-based integration tests running against a real,
separate `ccmc_cloud_test` database (not mocked):

```powershell
dotnet test CCMC.sln
```

**Actual result observed in this session:**

```
Passed!  - Failed: 0, Passed: 105, Skipped: 0, Total: 105  - CCMC.Tests.dll (net8.0)
Passed!  - Failed: 0, Passed: 21,  Skipped: 0, Total: 21   - CCMC.Cloud.Api.Tests.dll (net8.0)
```

**126/126 passing**, 0 failures. (Older docs in this repo, e.g. `CLAUDE.md`,
quote 89+21=110 from an earlier point in the project's history — the client
suite has since grown to 105; the number above is what was actually run and
observed in this session, not the older figure.)

Run individually:

```powershell
dotnet test tests/CCMC.Tests/CCMC.Tests.csproj              # no PostgreSQL needed
dotnet test tests/CCMC.Cloud.Api.Tests/CCMC.Cloud.Api.Tests.csproj  # needs ccmc_cloud_test reachable
```

---

# PART E — TROUBLESHOOTING

## 22. PostgreSQL connection refused

- `Get-Service -Name postgresql*` — start if not `Running` (§5).
- Confirm port 5432: `psql -U postgres -h localhost -p 5432 -c "SELECT 1;"`.
- Confirm credentials/database name match §7 exactly (`ccmc_cloud_dev`,
  `postgres`/`postgres`) or override via `ConnectionStrings__CcmcDb`.

## 23. EF migration failure

- Confirm PostgreSQL is reachable first (§22).
- `--project` must be `CCMC.Cloud.Infrastructure`, `--startup-project` must
  be `CCMC.Cloud.Api` — swapping these is the most common CLI error.
- Confirm `dotnet ef --version` reports `8.0.11` (§8).

## 24. Cloud API won't start

- **Port already in use**: `Get-NetTCPConnection -LocalPort 5000 -State
  Listen` to find and stop a stale instance
  (`Stop-Process -Id <OwningProcess> -Force`).
- **Missing config**: `Program.cs` throws `InvalidOperationException` at
  startup naming exactly which of `ConnectionStrings:CcmcDb` / `Jwt:Secret`
  is absent — read the console output.
- **Database unreachable**: `db.Database.Migrate()` runs at startup in every
  environment; a PostgreSQL problem surfaces here before the API ever binds
  a port.

## 25. Windows app says it cannot reach the cloud / admin or manager login fails

This was this repository's actual bug — see §12 for the full root-cause
writeup. If it recurs (e.g. after further edits to `launchSettings.json`):

1. Confirm the API is actually listening where the client expects it:
   `Invoke-RestMethod http://localhost:5000/health` — independent of the
   WPF app.
2. Confirm `src/CCMC.Desktop/appsettings.json`'s `CloudApi:BaseUrl` and
   wherever the API actually bound (console output: `Now listening on:
   ...`) name the **same** host, port, and scheme.
3. Check `%LocalAppData%\CCMC\logs\ccmc-yyyyMMdd.log` —
   `HttpCloudApiClient` logs the exact target login URL and outcome
   category (never the password/token) for every attempt.
4. If you see `Login succeeded (OFFLINE)` for an account you expected to hit
   the cloud, that means the request never reached the API at all — check
   #1/#2 again. A genuine online rejection (`401`) is never silently retried
   offline; only a network-level failure triggers the offline path.

## 26. Operator login unexpectedly uses offline credentials

An offline credential is cached only after a prior *successful online*
login for that exact email, on this exact machine, under this exact Windows
user account (DPAPI `CurrentUser` scope). If you need to prove a login is
genuinely going online (not silently reusing a cache), either watch the log
for `Login succeeded (online)` vs `(OFFLINE)`, or temporarily stop the Cloud
API and confirm the *specific* account you're testing fails cleanly if it
has never logged in online on this machine before (as `operator2` did in
§20) — the presence of a cache is not proof the cloud path was ever tried
successfully.

## 27. COM port / device issues

Out of scope for this pass (no hardware was connected or required to fix
login/API startup). See `CLAUDE.md` "Hardware Verification" and "Known
Limitations" for the current, unchanged state of the Videocon
scale/COM4/2400 default and the still-undecoded milk analyser.

---

# PART F — QUICK START

Everything below was run, in this order, in this session, against a machine
that already had PostgreSQL 16 installed and running with the default
`postgres`/`postgres` local credentials:

```powershell
# 1. PostgreSQL already running; databases already existed (safe to re-run):
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_dev;"
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_test;"

# 2. Migrations apply automatically at API startup - manual step optional:
dotnet ef database update `
  --project src/CCMC.Cloud.Infrastructure/CCMC.Cloud.Infrastructure.csproj `
  --startup-project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj

# 3. Start the Cloud API - a bare `dotnet run` now correctly binds port 5000:
dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj

# 4. In a second terminal, verify:
Invoke-RestMethod http://localhost:5000/health      # -> Healthy
Invoke-RestMethod http://localhost:5000/health/db   # -> Healthy

# 5. In a third terminal, start the Windows application:
dotnet run --project src/CCMC.Desktop/CCMC.Desktop.csproj

# 6. Log in with any seeded account, e.g.:
#    admin@ccmc.local / Admin@12345
#    manager1@ccmc.local / Manager@12345
#    operator1@ccmc.local / Operator@12345

# 7. Run the full test suite:
dotnet test CCMC.sln   # -> 126/126 passing (105 + 21)
```

---

## What changed to make this work

**One file, two lines** —
`src/CCMC.Cloud.Api/Properties/launchSettings.json`: the default `http` and
`https` launch profiles' port was changed from `5179`/`7123` to `5000`, so a
plain `dotnet run` matches the Windows client's already-correct, unchanged
`CloudApi:BaseUrl`. No other source, configuration, database, or seed change
was necessary — the Cloud API, its database, its migrations, its
development seed, JWT auth, and the Windows client's authentication/offline
logic were all already correct and are all verified working end-to-end
above; they were simply never reachable from the Windows app by default
before this fix.
