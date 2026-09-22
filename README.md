# CCMC — Chilling Centre Milk Collection (Windows Application)

A native Windows desktop application for dairy chilling centres: an
operator weighs incoming milk (RS232 weighing scale), tests its quality
(RS232 milk analyser), the app validates it against configured quality
rules, and the resulting transaction is saved locally and synchronized to
a central cloud API when connectivity allows.

This document is a practical developer/operator guide for the code in
this repository, on the `windows-application` branch. For the full
architecture rationale and decision history, see `STATUS.md` (engineering
progress log) and `context.md` (current technical context).

## 1. Overview

- **Product:** operator-facing reception workflow at a chilling centre -
  select source and vehicle, read (or manually enter) weight and quality,
  validate against quality rules, save, and sync.
- **Local-first:** a complete reception must be captured and saved
  entirely offline. Sync to the cloud happens afterwards, whenever
  connectivity is available.
- **No web application.** This Windows app is the only operator-facing
  application in the target architecture - there is no browser UI,
  Electron, or React frontend.

## 2. Target Architecture

```
PHYSICAL DEVICES (RS232: weighing scale, milk analyser)
        |
        v
NATIVE WINDOWS APP (C# / .NET 8 / WPF)
  - local operational workflows
  - device communication/parsing
  - SQLite local database
  - offline operation
  - sync
        | HTTPS
        v
CC-MC CLOUD API (ASP.NET Core 8 / EF Core, this repository - see §29)
        |
        v
PostgreSQL
```

The Windows app **never** connects directly to PostgreSQL - all cloud
communication goes through the cloud API's HTTP contract. SQLite is the
local **operational** store (not a disposable cache); PostgreSQL remains
the cloud **authoritative** database. No MVVM is used - WPF windows call
directly into application-layer services from code-behind.

**The cloud API now lives in this repository too** (added after the
initial Windows-app build - see §29 "CC-MC Cloud Backend"). It is a
**completely fresh** ASP.NET Core implementation - it does not reuse, call
into, or depend on the old NestJS API/Node gateway/React web app that
exist on the unrelated `pranav-dev` branch.

## 3. Solution / Project Structure

```
CCMC.sln
src/
├── CCMC.Domain/          Entities, enums, value objects, device interfaces,
│                         domain services (QualityValidationService). No project references.
├── CCMC.Contracts/       Wire DTOs mirroring the cloud API's actual contract,
│                         including the custom JSON converters for cloud
│                         decimal fields. No project references. Shared
│                         verbatim by BOTH the Windows client and the cloud
│                         API below (guarantees wire-format agreement).
├── CCMC.Application/     Abstractions (repository/cloud-client/device-manager
│                         interfaces) + workflow services (Reception, Sync,
│                         Auth, MasterData). References Domain + Contracts.
├── CCMC.Infrastructure/  SQLite (connection factory, migrations, repositories),
│                         Serial (COM port ownership), Devices (adapters,
│                         DeviceManager), Sync (HttpCloudApiClient), Auth
│                         (offline credential store), Logging (file logger).
│                         References Domain + Application + Contracts.
├── CCMC.Desktop/         WPF UI (code-behind only), DI composition root
│                         (App.xaml.cs), appsettings.json. References all
│                         four projects above.
│
│                         --- CC-MC Cloud Backend (see §29) ---
├── CCMC.Cloud.Domain/        Cloud-side entities/enums/domain services. No
│                              project references. Independent of CCMC.Domain
│                              (different assembly, different namespace,
│                              deliberately not shared - see §29.2).
├── CCMC.Cloud.Infrastructure/ EF Core (CcmcDbContext, migrations), Auth
│                              (password hashing, JWT), Seed (development
│                              seeder). References Cloud.Domain.
├── CCMC.Cloud.Application/   Auth/RBAC (UserContextService,
│                              CentreAccessGuard), Reception (idempotent
│                              create/override), MasterData, Dashboard,
│                              Audit services. References Cloud.Domain +
│                              Cloud.Infrastructure + CCMC.Contracts.
└── CCMC.Cloud.Api/            ASP.NET Core Web API - controllers, JWT
                                bearer auth, permission-based authorization,
                                Swagger, health checks, DTO mapping.
                                References Cloud.Application + Contracts.
tests/
├── CCMC.Tests/              xUnit test suite for the Windows app (all layers).
└── CCMC.Cloud.Api.Tests/    xUnit integration test suite for the cloud API
                              (WebApplicationFactory against a real
                              PostgreSQL test database - see §29.11).
Doc/
└── CCMC_BRD_and_Technical_Design_v2.docx   Business Requirements Document (source of truth).
```

**Naming note:** the cloud projects are named `CCMC.Cloud.*` rather than a
bare `CCMC.Domain`/`CCMC.Application`/etc. specifically to avoid an
assembly-name collision with the pre-existing Windows-client projects of
the same base names - both sets of projects now live in the same solution
and same `src/` folder.

## 4. Prerequisites

- **Windows** (this app uses `System.IO.Ports` for serial communication and
  Windows DPAPI for local credential protection - it does not run on
  Linux/macOS).
- **.NET 8 SDK** (see below) - the .NET 8 runtime alone is not sufficient
  to build.
- A physical weighing scale/milk analyser is **not** required for
  development - the app builds, runs, and shows all screens without any
  device attached (device reads simply report "unavailable" and fall back
  to manual entry).

## 5. .NET SDK Requirements

This app targets **.NET 8** (`net8.0` / `net8.0-windows`). Verify your SDK:

```
dotnet --list-sdks
```

You need an **8.x** SDK listed. If none is present, install it (verified
working on this project via winget):

```
winget install Microsoft.DotNet.SDK.8
```

If `dotnet` is not on your `PATH` after installing, it is typically at
`C:\Program Files\dotnet\dotnet.exe`.

## 6. Build

From the repository root:

```
dotnet build CCMC.sln
```

Expected result: `Build succeeded`, 0 warnings, 0 errors.

## 7. Test

```
dotnet test CCMC.sln
```

This runs **both** test suites: `CCMC.Tests` (Windows client, 89 tests)
and `CCMC.Cloud.Api.Tests` (cloud backend, 21 integration tests - these
require a real local PostgreSQL and a `ccmc_cloud_test` database; see
§29.11). Expected result (at the time of writing): **110 tests passing**,
0 failed, a few seconds total. The Windows-client suite covers domain
validation, SQLite persistence/idempotency/restart-recovery, HTTP client
deserialization (including the cloud's decimal-as-string quirk - see
§17), serial port ownership, device-manager initialization, offline
credential verification, and override-sync outbox behavior. The cloud
suite covers health checks, login/JWT/authorization, centre-scoping,
reception idempotency/conflict/quality-hold, and manager overrides,
against a real database - see `STATUS.md` "Tests Completed" for the exact
current count and breakdown.

If you only want to run the Windows-client suite (no PostgreSQL needed):

```
dotnet test tests/CCMC.Tests/CCMC.Tests.csproj
```

## 8. Publish

```
dotnet publish src/CCMC.Desktop/CCMC.Desktop.csproj -c Release -r win-x64 --self-contained false -o publish/CCMC.Desktop
```

This produces a framework-dependent deployment in `publish/CCMC.Desktop/`
- the target machine needs the **.NET 8 Desktop Runtime** installed (not
  just the SDK). There is no installer yet - see §17 "Deferred Items".

## 9. Running the Windows Application

**For development** (builds and runs in place):

```
dotnet run --project src/CCMC.Desktop/CCMC.Desktop.csproj
```

**From a published build:**

```
publish\CCMC.Desktop\CCMC.Desktop.exe
```

On first launch the app creates its local data directory automatically
(see §13) and applies its SQLite schema. You will land on the **Sign In**
screen.

## 10. Current Login Procedure

The Windows app authenticates against a cloud API over HTTPS
(`POST /auth/login`). **A cloud API now exists in this repository** - see
§29 "CC-MC Cloud Backend" - a fresh ASP.NET Core implementation, entirely
independent of the old NestJS API that exists only on the separate,
historically unrelated `pranav-dev` branch (which per this project's
branch rules, §27, must not be checked out, merged, or modified from
here).

**Practical consequence:** to actually log in - online or offline (offline
login requires at least one prior successful online login, see §20) - you
need a running instance of the cloud API (§29.6) reachable at the URL
configured in `src/CCMC.Desktop/appsettings.json` (`CloudApi:BaseUrl`).
Point it at `http://localhost:8081/` (the Docker Compose API port — see §29.16
"Deployment Direction") to use the local dev instance. This is already the
committed default in `appsettings.json`.

### 11. Development Credentials

The cloud backend's development seeder (§29.7) creates these accounts
against a **fresh local database** - they are not shared with, or copied
from, the old `pranav-dev` seed script:

| Email | Password | Role | Centre |
|---|---|---|---|
| `admin@ccmc.local` | `Admin@12345` | Admin | All centres |
| `manager1@ccmc.local` | `Manager@12345` | Manager | Bangalore (BLR-CC-01) |
| `operator1@ccmc.local` | `Operator@12345` | Operator | Bangalore (BLR-CC-01) |
| `operator2@ccmc.local` | `Operator@12345` | Operator | Mysore (MYS-CC-01) |

These are **development-only** values, seeded only when
`ASPNETCORE_ENVIRONMENT=Development` (§29.7) - they must never be treated
as, or reused as, production credentials, and never reused as any
`Bootstrap__*Password` value (§29.15/HOW_TO_RUN.md §9) - a real bootstrap
password must be a unique, strong value that does not appear anywhere in
this repository. Nothing in `CCMC.Desktop`
hardcodes, assumes, or falls back to any of these - the app has no
knowledge of them at all until you sign in against a running cloud API
that has actually run this seed.

### 12. If You Don't Have the Cloud API Running

The Windows app cannot complete a real login without one. To exercise the
full login → reception → sync flow end-to-end: follow §29 "CC-MC Cloud
Backend" to start PostgreSQL, apply migrations, seed development data, and
run the API locally, then point `src/CCMC.Desktop/appsettings.json`'s
`CloudApi:BaseUrl` at it (§29.10).

No safe substitute (e.g., a hardcoded bypass, a fake local account, or an
`admin/admin`-style default) has been added to this Windows application,
and none should be - the app must not silently pretend a credential exists
when it doesn't.

## 13. Local Data Locations

All local, per-machine data lives under:

```
%LocalAppData%\CCMC\
```

Specifically:

| What | Path |
|---|---|
| SQLite database | `%LocalAppData%\CCMC\ccmc.db` (+ `-wal`/`-shm` files while open) |
| Structured logs | `%LocalAppData%\CCMC\logs\ccmc-yyyyMMdd.log` |
| Raw device captures | `%LocalAppData%\CCMC\captures\<device-id>_yyyyMMdd.capture.log` |

## 14. Logs

Plain tab-separated lines: `timestamp<TAB>level<TAB>category<TAB>message`.
The category is the .NET logger category (e.g.
`CCMC.Infrastructure.Devices.DeviceManager`), which doubles as the
Application/Device/Serial/Parser/Reception/Synchronization/Security
grouping - each of those concerns lives in its own class. Passwords,
access tokens, and other secrets are never logged - see `STATUS.md`
"Architecture Decisions" for what each log call site does and doesn't
include.

## 15. Raw Device Capture Location

Every raw byte actually received from a configured device is appended to
its own per-device, per-day capture file under `%LocalAppData%\CCMC\captures\`
(hex-dumped, one line per read, with a timestamp and byte count) -
this exists specifically so a real device protocol can eventually be
reverse-engineered from controlled captures. It is separate from the
general application log (§14), which only ever logs a byte *count*, never
the content.

## 16. Device Configuration

Open **Dashboard → Device Configuration**. For the selected device (scale
or analyser) you can set:

- **COM port** - a live dropdown of currently available ports
  (`System.IO.Ports.SerialPort.GetPortNames()`), with a **Refresh** button;
  also editable by hand if a port isn't currently enumerated.
- **Baud rate** - the standard set (110 / 300 / 600 / 1200 / 2400 / 4800 /
  9600 / 14400 / 19200 / 38400 / 57600 / 115200), also editable for a
  non-standard value.
- **Data bits** (5/6/7/8), **Parity** (None/Even/Odd/Mark/Space),
  **Stop bits** (1/1.5/2), **Flow control** (None/RTS-CTS/XON-XOFF/both).
- **Device/vendor name**, **Enabled** checkbox.
- **Test Connection** - opens the port with the currently entered
  (not-yet-saved) settings for real, reports success/failure, then closes
  it immediately. If the port is already owned by an active device
  connection elsewhere in the running app, this is reported explicitly
  rather than as a generic failure (COM ports are never double-opened).
- **Save** - persists to the local SQLite `device_configurations` table.
  Takes effect on the next application restart (device adapters are
  constructed once at startup).

**These are per-installation settings, not fixed assumptions** - a
different chilling centre can configure an entirely different COM port,
baud rate, or device. Nothing in the reception/business logic hard-codes
any serial parameter.

## 17. Current Verified Videocon Default Configuration

If no `DeviceConfiguration` row exists yet for the weighing scale, the app
falls back to this **physically verified** default (not a business
assumption - just a sensible starting point before an operator visits the
configuration screen):

| Setting | Value |
|---|---|
| Vendor | Videocon Precision Systems |
| COM Port | COM4 |
| Baud Rate | 2400 |
| Data Bits | 8 |
| Parity | None |
| Stop Bits | 1 |
| Flow Control | None |

This default is encoded once, as data, in
`CCMC.Domain.ValueObjects.SerialConfiguration.VerifiedWeighingScaleDefault`
- it is never hard-coded into reception or business logic, and is fully
overridable via §16.

## 18. Videocon Protocol Decoding — NOT YET IMPLEMENTED

The weighing scale **connects** for real at the settings above (or
whatever is configured), and any bytes it sends are captured for real
(§15). **No byte-level Videocon protocol decoder exists.** Reading a
weight will always report the device as "unavailable" with a message
naming how many bytes were received but not decoded, and the operator
must enter the weight manually. This is deliberate: no protocol may be
guessed. A real decoder requires controlled captures and/or manufacturer
documentation, neither of which exist in this repository yet. The exact
same situation applies to the milk analyser (§2, no verified protocol at
all yet, for any vendor).

## 19. How "Test Connection" Behaves

"Test Connection" (Device Configuration screen, §16) only proves the COM
port itself can be opened with the given serial parameters - it does
**not** prove the device speaks any particular protocol, and it never
returns a fabricated weight/quality reading. A successful test means
"Windows can open this port at this baud rate right now"; nothing more.

## 20. Offline Login Behavior

- **First login for any account must be online** - the cloud remains
  authoritative. On a successful online login, the app caches (locally,
  DPAPI-protected under the current Windows user, Argon2id-hashed - never
  the plaintext password, never the cloud access token) a verifier plus a
  snapshot of that user's roles/permissions/centre access.
- **If the cloud cannot be reached** on a later login attempt, the app
  falls back to verifying the entered password against that cached
  verifier. On success, the operator is signed in **offline** - the
  Dashboard clearly shows "OFFLINE MODE (sync paused)".
- **A genuine online rejection (wrong password, disabled account) is never
  second-guessed by the offline cache** - if the cloud was actually
  reached and said no, that is authoritative, full stop.
- **Offline sessions never hold a usable access token** - synchronization
  is paused (not attempted) while signed in offline. Local reception
  capture/save continues to work fully.
- **To resume sync**, click **"Sign in online"** on the Dashboard (visible
  only in offline mode) once connectivity returns - this signs out of the
  offline session and reopens the login screen so you can authenticate
  online again, which refreshes your roles/permissions/centre access from
  the cloud and resumes synchronization.

## 21. Synchronization Behavior

- Every locally captured reception is saved to SQLite **before** any sync
  attempt - a cloud outage never loses a completed local transaction.
- Sync only runs while a **signed-in, online** operator session exists
  (it uses that operator's own cloud access token - there is no separate
  unattended/service-account credential, by explicit product decision).
- Idempotent retries: each reception carries a stable, once-generated
  local idempotency key; the cloud's `POST /reception` already supports
  this (`INSERT ... ON CONFLICT DO NOTHING`), so a lost-response retry can
  never create a duplicate cloud transaction.
- **Manager overrides** (resolving a HOLD to ACCEPTED/REJECTED) sync
  through their own durable outbox, only once the underlying reception
  itself has synced (has a cloud transaction id). **Important limitation:**
  the cloud's override endpoint has no idempotency-key mechanism (unlike
  reception creation) - see `STATUS.md` "Architecture Decisions" for the
  documented contract gap this implies. A terminal/4xx response on an
  override retry is marked FAILED for manual/ops review, never silently
  retried or silently assumed successful.
- A tick runs automatically every 30 seconds while the Dashboard is open,
  plus once immediately at startup; "Synchronization Status" also has a
  manual "Sync Now" button.

## 22. Troubleshooting

- **"No weighing scale is configured" / "Analyser unavailable"** - either
  no device is configured yet (§16), or the configured protocol isn't
  decoded (§18) - this is expected until real protocol work is done; use
  manual entry.
- **Login always fails** - there is no cloud API reachable at the
  configured `CloudApi:BaseUrl` (§10/§12). Check `appsettings.json` and
  confirm something is actually listening there.
- **Offline login fails even though online login worked before** - offline
  login only works for an account that has previously logged in online
  *on this machine*, under *this Windows user account* (DPAPI is
  per-user). A different Windows account, or a machine that has never
  seen a successful online login for that email, has nothing cached.
- **Reception won't save ("No quality rule resolved for ...")** - the
  local quality-rules cache is empty; it's populated from the cloud on
  each successful **online** login (`MasterDataSyncService`). Sign in
  online at least once with connectivity.
- **"Port already owned by an active device connection"** (Device
  Configuration → Test Connection) - another part of the running app
  already has that COM port open; this is the one-owner-per-port
  enforcement working as intended, not a bug.

## 23. Common Startup/Device Issues

- **App won't start / crashes immediately** - check
  `%LocalAppData%\CCMC\logs\ccmc-yyyyMMdd.log` for the first `Error`
  entry; startup failures are logged before the login window would
  normally appear.
- **A device never connects** - verify the COM port isn't in use by
  another application (Device Manager, a terminal program, etc.) and that
  the cable/driver is actually present; "Test Connection" (§19) will
  report the real underlying exception message.
- **SQLite database looks stale after an upgrade** - the schema uses a
  real, ordered, tracked migration system (`schema_migrations` table); it
  upgrades forward automatically on next startup. It does not go backward.

## 24. Current Known Limitations

- No installer - see §26. `dotnet publish` output is a folder, not a
  setup experience.
- No hardware-in-the-loop testing (no physical scale/analyser attached to
  the development environment used to build this).
- `PermissionCodes` (mirrored from the cloud) are not yet wired into any
  window's button visibility - every signed-in operator currently sees
  every button; the cloud API remains the real enforcement point either
  way.
- Sources/Vehicles screens are read-only (cached from the cloud; no
  create/edit UI built yet).
- Decimal values are stored in SQLite `REAL` (double) columns, not an
  exact fixed-point type - empirically safe for this domain's realistic
  value ranges (verified directly), but an implicit assumption, not a
  guarantee, if far higher precision were ever required.

## 25. Deferred Implementation Items

Explicitly deferred, not oversights - see `STATUS.md` "Decisions Pending"
for the full reasoning behind each:

- **Real Videocon scale protocol decoder** - blocked on physical device
  captures and/or manufacturer documentation.
- **Milk analyser protocol decoder** - no vendor/protocol confirmed at
  all yet.
- **Installer** - WiX MSI is the chosen direction (§26); not built.
- **Any unattended/Windows-service sync** - explicitly rejected by product
  decision; sync always rides the currently signed-in operator's own
  token.
- **Videocon vs. ESSAE hardware discrepancy** - an older, different
  "CEO-confirmed" serial configuration exists in the legacy gateway docs
  on `pranav-dev` for what appears to be different equipment; not
  reconciled, needs explicit human confirmation before real scale work
  starts.

## 26. Installer

**Chosen deployment technology: WiX MSI.** Not implemented in this pass -
`dotnet publish` (§8) produces a deployable folder only. Building the
actual WiX packaging project (install location, shortcuts, prerequisite
.NET Desktop Runtime check, upgrade/uninstall behavior) remains a future
implementation step.

## 27. Development Workflow / Branch Rules

- **Work happens on `windows-application` only.** The legacy branch
  `pranav-dev` (a completely separate git history - no common ancestor
  with `windows-application`) holds the previous Node/NestJS/React
  implementation. It is a reuse/reference target for the cloud API's
  contract, never something to check out, merge, cherry-pick from, or
  modify from this branch.
- Standard workflow: make changes, `dotnet build CCMC.sln`, `dotnet test
  CCMC.sln`, and if the change affects the published app, `dotnet publish`
  (§8) and a real launch to confirm (check the log, §14).
- Do not commit build artifacts (`bin/`, `obj/`, `publish/`) - already
  excluded via `.gitignore`.
- Commits are not made automatically - only when explicitly requested.

## 28. Security Notes

- **No plaintext passwords are ever stored.** Online login sends the
  password directly to the cloud over HTTPS and never persists it; the
  offline-login cache stores only an Argon2id verifier (never the
  password itself).
- **No access tokens are used as an offline credential substitute** - an
  offline session's access token is always empty; sync is paused, not
  attempted, until a real online re-login.
- **Offline credential material is DPAPI-protected** (`CurrentUser`
  scope) at rest in SQLite - readable only by the same Windows user
  account that created it.
- **Passwords and access tokens are never written to the structured log**
  (§14) - verified directly in the relevant call sites' code comments.
- **This app never holds PostgreSQL credentials** and never connects to
  PostgreSQL directly - all cloud access goes through the HTTP API.
- Development seed credentials (§11) are development-only values created
  by the cloud backend's own seeder (§29.7) and must never be used,
  assumed, or reproduced as production credentials.

See §29.14 for the cloud backend's own, more detailed security review
notes (password hashing, JWT secret handling, SQL injection, IDOR/
cross-centre leakage, CORS, error verbosity).

## 29. CC-MC Cloud Backend

A fresh ASP.NET Core 8 + EF Core + PostgreSQL implementation of the cloud
API the Windows client talks to. **This is not a port of, and does not
call into, the old NestJS API / Node gateway / React web app** that exist
on the unrelated `pranav-dev` branch - nothing from those was imported or
copied. The projects live under `src/CCMC.Cloud.*` and `tests/CCMC.Cloud.Api.Tests`
(see §3).

### 29.1 Architecture

```
Windows WPF app (CCMC.Desktop)
        | HTTPS (System.Net.Http.HttpClient)
        v
CCMC.Cloud.Api          ASP.NET Core Web API - controllers, JWT bearer
                        auth, permission-based authorization, Swagger,
                        health checks, exception → HTTP error mapping.
        |
        v
CCMC.Cloud.Application  Auth/RBAC, Reception (idempotent create/override),
                        MasterData (centres/sources/vehicles/quality
                        rules), Dashboard summaries, Audit logging.
        |
        v
CCMC.Cloud.Infrastructure  EF Core DbContext + migrations, password
                        hashing, JWT issuance, development seeder.
        |
        v
CCMC.Cloud.Domain       Entities, enums, QualityValidationService
        |
        v
PostgreSQL
```

`CCMC.Contracts` (the Windows client's own wire-DTO project) is referenced
directly by `CCMC.Cloud.Application`/`CCMC.Cloud.Api` and used **as-is**
for every request/response body - this guarantees the cloud API's wire
format matches exactly what `HttpCloudApiClient` already sends/parses,
with zero duplicate DTO definitions to drift out of sync.

The Windows app **never** connects to PostgreSQL directly - only this API
does.

### 29.2 Why `CCMC.Cloud.*` names, and why a flipped dependency direction

The cloud projects are named `CCMC.Cloud.Domain/Application/Infrastructure/Api`
rather than bare `CCMC.Domain/Application/Infrastructure/Api` **specifically**
to avoid an assembly-name collision with the pre-existing Windows-client
projects of the same base names, now that both live in the same solution.

`CCMC.Cloud.Application` references `CCMC.Cloud.Infrastructure` (not the
other way around, unlike the Windows client's own Application→Infrastructure-
via-interfaces pattern) - this lets Application services use the concrete
`CcmcDbContext` directly, deliberately without an extra repository-
interface layer the server side doesn't need.

### 29.3 Database Design

All monetary/measurement columns use PostgreSQL `NUMERIC(p,s)` -
**never** `float`/`double` (`real`/`double precision`):

| Column | Precision | Entity |
|---|---|---|
| `QuantityKg` | `NUMERIC(10,2)` | MilkReceptionTransaction |
| `Fat`, `Snf`, `Temperature` | `NUMERIC(5,2)` | MilkReceptionTransaction |
| `Vehicle.CapacityKg` | `NUMERIC(10,2)` | Vehicle |
| `QualityRule.MinValue`/`MaxValue` | `NUMERIC(6,2)` | QualityRule |

Key tables (see `CcmcDbContext` for the full model): `Users`, `Roles`,
`Permissions`, `RolePermissions`, `UserRoles`, `UserCentreAssignments`,
`ChillingCentres`, `Sources`, `Vehicles`, `QualityRules` (nullable
`CentreId` = a global rule), `MilkReceptionTransactions`,
`TransactionOverrides`, `AuditLogs`. All foreign keys use
`DeleteBehavior.Restrict` or `.Cascade` as appropriate; enums are stored
as strings (not integers) for readability/stability across migrations.

**Idempotency is enforced at the database level**, not by an
application-level check-then-insert: `MilkReceptionTransactions` has a
unique index on `LocalIdempotencyKey`
(`ix_milk_reception_transactions_local_idempotency_key`, explicitly
named so the code can recognize a violation of *this specific* constraint
rather than any unique-constraint failure). See §29.6.

Migrations are real, tracked EF Core code-first migrations
(`Persistence/Migrations/`), applied via `db.Database.Migrate()` -
**`EnsureCreated()` is never used**.

### 29.4 Authentication & RBAC

- `POST /auth/login` - accepts `{email, password}`, returns
  `{accessToken, expiresAt, user: {id, email, fullName, roles[],
  permissions[], centreAccess: {allCentres, centreIds[]}}}` (exact shape
  of `CCMC.Contracts.Auth.LoginResponseDto`, already what the Windows
  client's `HttpCloudApiClient` expects).
- Passwords are hashed with ASP.NET Core Identity's `PasswordHasher<T>`
  (PBKDF2) - **never** stored or logged in plaintext. Unknown email and
  wrong password both return the same `401` with the same message (no
  email-enumeration signal). A wrong-password check runs against a real
  dummy hash even for an unknown email, so the two cases take
  comparable time.
- JWTs carry only `sub` (user id), `email`, and `jti` - **not** roles or
  permissions, which are re-loaded fresh from the database on every
  request via `UserContextService` (so a permission/role change takes
  effect on the very next request, not only at next login).
- **Gotcha already hit and fixed** (see `Program.cs`): ASP.NET Core's JWT
  handler silently renames `sub`/`email` to legacy XML-namespace claim
  URIs unless `options.MapInboundClaims = false` is set - without it,
  every authenticated request fails with "No authenticated user" despite
  a genuinely valid token.
- **Roles** (minimum, seeded): `Operator`, `Manager`, `Admin`. Permissions
  are dynamic policy names of the form `Permission:<CODE>` (e.g.
  `Permission:RECEPTION_OVERRIDE`), enforced via a custom
  `[RequirePermission(PermissionCodes.X)]` attribute + a custom
  `IAuthorizationPolicyProvider` - not a fixed, hardcoded policy list.
- **Centre-scoping is enforced server-side, always** - `CentreAccessGuard.AssertCanAccess`
  checks the authenticated user's actual centre assignments (loaded from
  the database, never trusted from the client) before any centre-scoped
  read/write; a client-supplied `centreId` a user isn't assigned to
  always yields `403`, not filtered/empty results.

### 29.5 API Endpoints

All routes verified against the Windows client's actual
`HttpCloudApiClient` calls - none invented:

| Method & Route | Permission | Notes |
|---|---|---|
| `POST /auth/login` | (none - public) | Returns JWT + user/roles/permissions/centre access |
| `GET /centres` | any authenticated user | Centre-scoped list |
| `GET /sources` | `SOURCE_VIEW` | Centre-scoped |
| `GET /sources/{id}` | `SOURCE_VIEW` | |
| `POST /sources` | `SOURCE_CREATE` | |
| `PATCH /sources/{id}` | `SOURCE_EDIT` | |
| `GET /vehicles` | `VEHICLE_VIEW` | Centre-scoped |
| `GET /vehicles/{id}` | `VEHICLE_VIEW` | |
| `POST /vehicles` | `VEHICLE_CREATE` | |
| `PATCH /vehicles/{id}` | `VEHICLE_EDIT` | |
| `GET /quality-rules` | `QUALITY_RULE_VIEW` | Centre-specific + global rules |
| `PATCH /quality-rules/{id}` | `QUALITY_RULE_CONFIGURE` | No create endpoint - rules are seeded/managed by id |
| `GET /rate-formula-settings` | `RATE_FORMULA_VIEW` | BRD v5.0 §25 - centre-specific + global rate formula config; synced to the Windows client for offline calculation |
| `PUT /rate-formula-settings` | `RATE_FORMULA_CONFIGURE` | Upsert by centre (body's `centreId` may be `null` for the global default) - no numeric default is ever seeded, so Rate/Amount is 0 until this is called at least once for a centre |
| `GET /reception` | `RECEPTION_VIEW` | Centre-scoped list |
| `GET /reception/{id}` | `RECEPTION_VIEW` | `403` if outside caller's centre access |
| `POST /reception` | `RECEPTION_CREATE` | Idempotent create - see §29.6. Returns **201** for both `created` and `duplicate` outcomes (never 200), matching the client's `HttpResponseClassifier` |
| `POST /reception/{id}/override` | `RECEPTION_OVERRIDE` | Manager override - see §29.6 |
| `GET /dashboard/summary?centreId=` | `DASHBOARD_VIEW` | Today's (IST calendar day) counts |
| `GET /audit-logs` | `AUDIT_VIEW` | Centre-scoped |
| `GET /health` | (none) | Liveness only, no DB check |
| `GET /health/db` | (none) | Proves real PostgreSQL connectivity |

Error responses use a flat custom shape (not RFC7807 ProblemDetails),
matching what the client already parses
(`CCMC.Contracts.Dtos.ReceptionConflictResponseDto`):
```json
{ "message": "...", "conflictingFields": ["QuantityKg"], "existingTransactionId": 42 }
```
`conflictingFields`/`existingTransactionId` are present only on a `409`
idempotency conflict. Exceptions map to status codes as: validation→400,
unauthenticated→401, centre-access-denied→403, not-found→404,
idempotency/business conflict→409, anything else→500 with a generic
"An unexpected error occurred." message - **no stack trace or database
detail is ever leaked** to the client.

### 29.6 Synchronization & Idempotency Design

- `CreateReceptionRequestDto.LocalIdempotencyKey` (optional, already
  defined in `CCMC.Contracts`) is the mechanism: the Windows client
  generates one stable key per locally-captured reception, once, and
  resends the same key on every retry.
- On `POST /reception`, if a key is supplied, the insert happens inside a
  transaction; a `DbUpdateException` wrapping a
  `Npgsql.PostgresException` with `SqlState == UniqueViolation` **and**
  `ConstraintName` matching the named idempotency index is caught,
  distinguishing a genuine duplicate-key collision from any other
  constraint violation. The existing row is then re-fetched and compared
  field-by-field (`CentreId`, `SourceId`, `VehicleId`, `QuantityKg`,
  `Fat`, `Snf`, `Temperature` - **not** `OperatorUserId`, since the same
  operator retrying is expected):
  - **Identical payload** → `201` with `outcome: "duplicate"`, same
    transaction id as the original. Safe to retry indefinitely.
  - **Different payload, same key** → `409` with `conflictingFields`
    listing exactly which fields differ, plus `existingTransactionId` -
    this is a caller bug (key reused for genuinely different data), not
    a legitimate retry, and is never silently accepted.
- Quality validation never auto-rejects: `QualityValidationService`
  returns `ACCEPTED` (all parameters in range) or `HOLD` (any parameter
  out of range, with a `reason`) - **`REJECTED` is only ever set via an
  explicit manager override**, never by the create path.
- **Manager override** (`POST /reception/{id}/override`): requires the
  transaction to currently be `HOLD`; `NewStatus` must be `ACCEPTED` or
  `REJECTED`. If the transaction is not `HOLD` (e.g. a retried override
  request after the first one already resolved it), the request fails
  with `409` - `BusinessConflictException` - documenting a **known
  contract gap**: unlike reception creation, the override endpoint has
  **no idempotency key**, so a genuine network-retry of an override
  cannot be distinguished from a stale/duplicate request purely
  server-side. The Windows client's own outbox already treats a
  terminal/4xx override response as `FAILED` for manual review rather
  than silently retrying or silently assuming success (see §21) - this
  is the correct client-side mitigation for that gap, not a client bug.
  Every successful override writes a `TransactionOverride` row and an
  `AuditLog` entry in the same database transaction as the status
  change.

### 29.7 Development Seed Data

Runs automatically at startup, but **only** when
`ASPNETCORE_ENVIRONMENT=Development` (idempotent - safe to run repeatedly,
upserts by natural key). Seeds:

- **Permissions/Roles**: `Operator`, `Manager`, `Admin`, with the
  permission grants listed in `DevelopmentSeeder.RolePermissions` (a
  fresh judgment call informed by BRD v2 §10/§14 - not copied from any
  prior codebase).
- **Centres**: `BLR-CC-01` (Bangalore), `MYS-CC-01` (Mysore).
- **Users** (see §11 for the table): `admin@ccmc.local` (Admin, all
  centres), `manager1@ccmc.local` (Manager, Bangalore),
  `operator1@ccmc.local` (Operator, Bangalore), `operator2@ccmc.local`
  (Operator, Mysore).
- **Global quality rules** (`CentreId = null`, apply everywhere), values
  taken directly from BRD v2 §10: Fat 3.0-6.0, SNF 8.0-10.0, Temperature
  0-10 (°C).
- **Sources/Vehicles**: one of each per centre (`SRC-BLR-001`/
  `SRC-MYS-001`, vehicles `KA01AB1234`/`KA09CD5678`) so reception can be
  exercised immediately after seeding.

### 29.8 Prerequisites

- **PostgreSQL** (verified against v16 locally) - **not** mandatory to
  run via Docker; any local install works as long as it's reachable at
  the configured connection string.
- **.NET 8 SDK** (same as §5).
- **`dotnet-ef` global tool**, pinned to match the EF Core package
  version used here:
  ```
  dotnet tool install --global dotnet-ef --version 8.0.11
  ```
  (If already installed at a different version: `dotnet tool update --global dotnet-ef --version 8.0.11`.)

### 29.9 PostgreSQL Setup (Local Development)

Create the two databases used in development (`ccmc_cloud_dev` for
running the API, `ccmc_cloud_test` for the automated integration tests -
kept separate so running tests never touches your working dev data):

```
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_dev;"
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_test;"
```

The default dev connection string (`appsettings.Development.json`)
assumes `Host=localhost;Port=5432;Username=postgres;Password=postgres` -
adjust it (or override via the `ConnectionStrings__CcmcDb` environment
variable) to match your local PostgreSQL superuser credentials.

### 29.10 Configuration

`appsettings.json` (committed, no secrets) intentionally has **no**
`ConnectionStrings`/`Jwt` section - only `appsettings.Development.json`
(also committed, but explicitly documented as dev-only placeholders, e.g.
`Jwt:Secret = "dev-only-local-signing-secret-not-a-real-secret-..."`)
supplies them, and only when `ASPNETCORE_ENVIRONMENT=Development`. Any
other environment **must** supply both via environment variables
(`ConnectionStrings__CcmcDb`, `Jwt__Secret`) or another real secret store
- the app throws `InvalidOperationException` at startup if either is
missing, rather than silently falling back to an insecure default.

**Windows client configuration** - point `src/CCMC.Desktop/appsettings.json`'s
`CloudApi:BaseUrl` at wherever you run this API. The committed default is
`http://localhost:8081/` (the Docker Compose port, §29.16) - only change it
if you're running the API a different way, e.g.:
```json
{ "CloudApi": { "BaseUrl": "http://localhost:5000/" } }
```

### 29.11 Migrations & Running Locally (without Docker)

> **Docker Compose (§29.16) is the primary, recommended way to run this API** -
> `docker compose -p cc-mc up -d --build` with `.env.docker`/`.env.neon`. The
> steps below are a secondary alternative for running the API directly with
> `dotnet run` against a manually-installed local PostgreSQL (§29.9), useful
> for debugging the API itself without a container in the loop. If you use
> this path, remember to point the Windows client's `CloudApi:BaseUrl` at
> whatever port you pick here (e.g. `5000`), not the Docker default `8081`.

From the repository root, with `dotnet-ef` on `PATH` (§29.8):

Apply migrations (creates/updates schema in `ccmc_cloud_dev`, reading the
connection string from `CCMC_DB_CONNECTION_STRING` env var if set, else
the same local-only fallback used by `CcmcDbContextFactory`):
```
dotnet ef database update \
  --project src/CCMC.Cloud.Infrastructure/CCMC.Cloud.Infrastructure.csproj \
  --startup-project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj
```

In practice this migration step is optional for local development -
**the API also applies pending migrations automatically at startup**
(`db.Database.Migrate()` in `Program.cs`, every environment, not just
Development), and the Development-only seed then runs immediately after.

Run the API:
```
dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj
```
This uses the `Development` environment by default (`launchSettings.json`)
and listens on `https://localhost:7123` and `http://localhost:5179` (the
`http`-only profile listens on `http://localhost:5179` alone). To pin an
exact address instead (what was used throughout this implementation's own
manual verification):
```
dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj --urls http://localhost:5000
```

### 29.12 Swagger / OpenAPI

Enabled only when `ASPNETCORE_ENVIRONMENT=Development`. With the API
running (§29.11), open:
```
http://localhost:5000/swagger
```
(substitute whichever host/port you actually ran it on). Click
**Authorize**, paste only the `accessToken` value returned by
`POST /auth/login` (Swagger UI adds the `Bearer ` prefix itself).

### 29.13 Health Checks

```
GET http://localhost:5000/health      -> liveness only, no DB dependency
GET http://localhost:5000/health/db   -> real PostgreSQL connectivity check
```
Both return the plain text body `Healthy` on success.

### 29.14 Testing

`tests/CCMC.Cloud.Api.Tests` is a real integration suite -
`WebApplicationFactory<Program>` boots the **actual** app startup
pipeline (real migrations, real Development seed) against a **separate**
database, `ccmc_cloud_test` (never `ccmc_cloud_dev`), so a test run never
disturbs your own local dev data. Requires PostgreSQL reachable exactly as
in §29.9 - these are not mocked/in-memory tests.

```
dotnet test tests/CCMC.Cloud.Api.Tests/CCMC.Cloud.Api.Tests.csproj
```

Current suite (21 tests, all passing):

- `HealthTests` (2): `/health` and `/health/db` both healthy.
- `AuthTests` (6): valid login returns token + user/roles/permissions/
  centre access; wrong password → 401; unknown email → 401 (same as
  wrong password); missing/invalid token → 401; login response never
  contains `passwordHash`.
- `AuthorizationTests` (5): Admin sees both seeded centres; an
  Operator sees only their own centre; an Operator lacking
  `RECEPTION_OVERRIDE` gets 403; a Manager who has it gets 404 (not 403)
  on a nonexistent transaction id - proving the permission check and the
  not-found check are distinct; an Operator cannot see the other centre's
  sources.
- `ReceptionTests` (8): valid create → 201/`created`; identical
  idempotency key retried → 201/`duplicate` with the same id; same key +
  different payload → 409 with `conflictingFields`; out-of-range quality
  → `HOLD`, never auto-rejected; cross-centre create → 403; a source
  belonging to a different centre → 400; a wrong-centre operator → 403;
  `GET /reception/{id}` for a transaction outside the caller's centre
  access → 403.

Run the **whole solution's** tests (Windows client + cloud, 110 total):
```
dotnet test CCMC.sln
```

### 29.15 Production Security Notes

- No plaintext passwords anywhere - hashed via `PasswordHasher<T>`
  (PBKDF2); never logged.
- No hardcoded secrets - `Jwt:Secret` and `ConnectionStrings:CcmcDb` must
  come from environment variables or a real secret store outside
  Development; the app fails fast at startup if they're missing rather
  than falling back to a default.
- SQL injection: all data access goes through EF Core's parameterized
  LINQ queries - no raw/interpolated SQL anywhere in this codebase.
- Authorization/IDOR: every centre-scoped read/write re-validates the
  authenticated user's actual centre assignments server-side
  (`CentreAccessGuard`) - a client-supplied id for a resource outside the
  caller's access always yields 403/404, never another centre's data.
- Tokens/passwords are never written to logs.
- Production (non-Development) responses never include a stack trace or
  raw exception message - only a generic message via
  `ExceptionHandlingMiddleware`.
- No CORS policy is configured - deliberate, since the only client is a
  native `HttpClient`-based Windows app, not a browser SPA (see the
  comment in `Program.cs`). Do not add a CORS policy unless a genuine
  browser client is introduced.

### 29.16 Deployment Direction

**Current status: containerized and verified locally (Docker + Docker
Compose + a real Neon PostgreSQL database, now with real production
accounts - see §29.15/§29.17); Render deployment is BLOCKED, not merely
"the next step."** A `Dockerfile` now exists, and the full path from
source to a running container against a real cloud PostgreSQL has been
proven - **the only missing piece is repository access**: the GitHub
repository is owned/controlled by the CEO, and the operator working on
this repo does not currently have the access needed to connect the
private repository to Render (2026-09-15). See HOW_TO_RUN.md §10 for the
exact ordered steps once access is obtained.

```
Windows WPF ──HTTPS──▶ CCMC.Cloud.Api (same image everywhere) ──Npgsql──▶ PostgreSQL
                              │
              Local Docker ───┼─── cc-mc-postgres (Docker Compose)
              Production ─────┴─── Neon (target: Render, BLOCKED on repository access)
```

The application binary/image is **identical** across every environment;
only environment variables change. No source-code branch exists for
"local" vs "production."

**Docker**: `Dockerfile` (repo root) builds `CCMC.Cloud.Api` as a
multi-stage image (`mcr.microsoft.com/dotnet/sdk:8.0` → `mcr.microsoft.com/dotnet/aspnet:8.0`).
`.dockerignore` excludes the Windows client projects, `tests/`, and
`appsettings.Development.json` (so no dev placeholder secret can ever end
up in the image, in any environment).

**Docker Compose** (`compose.yaml`, repo root) - project name `cc-mc`,
used for BOTH local-testing modes below (the SAME `api` service
definition; only which PostgreSQL it talks to changes):

| Service | Container name | Image | Runs in |
|---|---|---|---|
| API | `cc-mc` | `cc-mc-api` (built from `Dockerfile`) | Both modes |
| PostgreSQL | `cc-mc-postgres` | `postgres:16` | Docker mode only (see below) |

Both run on a dedicated `cc-mc-network` Docker network; the API reaches
local PostgreSQL via the service hostname `cc-mc-postgres`, never
`localhost`. PostgreSQL has a healthcheck, and the API's `depends_on`
waits for it to report healthy (not just "started") before starting,
since `db.Database.Migrate()` runs immediately at API startup.

**Environment configuration** - see `.env.example` for the full reference
of every variable CCMC.Cloud.Api consumes. `docker compose` auto-loads a
file literally named `.env` (no `--env-file` flag needed) - switching
mode means copying the mode-specific file over it (PowerShell):

| File | Committed? | Copy to `.env` for... |
|---|---|---|
| `.env.example` | Yes (placeholders only) | — reference only, never copied directly |
| `.env.docker` | **No** (gitignored) | Mode A: API → local `cc-mc-postgres` |
| `.env.neon` | **No** (gitignored) | Mode B: API → Neon PostgreSQL |

**Mode A — Docker** (API → local PostgreSQL):

```powershell
Copy-Item .env.docker .env -Force
docker compose -p cc-mc down
docker compose -p cc-mc up -d --build
```

Result: `WPF → http://localhost:8081 → cc-mc → cc-mc-postgres`. `.env.docker`
sets `COMPOSE_PROFILES=docker`, which activates the `cc-mc-postgres`
service's Compose profile.

**Mode B — Neon** (API → Neon PostgreSQL, no local Postgres container):

```powershell
Copy-Item .env.neon .env -Force
docker compose -p cc-mc down
docker compose -p cc-mc up -d --build
```

Result: `WPF → http://localhost:8081 → cc-mc → Neon PostgreSQL`.
`.env.neon` deliberately omits `COMPOSE_PROFILES`, so the `cc-mc-postgres`
service's profile is never activated and Compose does not start a
redundant local PostgreSQL container - verified directly (`docker compose
-p cc-mc ps` shows only `cc-mc` running in this mode).

In both modes, verify with:

```powershell
docker compose -p cc-mc config   # validate
docker compose -p cc-mc ps       # confirm which services are running
curl http://localhost:8081/health
curl http://localhost:8081/health/db
```

The variables themselves never change name or meaning between
environments - only their values (`.env.docker`/`.env.neon` set them,
`.env.example` documents them):

- `ASPNETCORE_ENVIRONMENT` - `Development` (Docker mode) or `Production` (Neon)
- `ASPNETCORE_URLS` - `http://0.0.0.0:8080` in both (never `localhost`-only)
- `ConnectionStrings__CcmcDb` - standard Npgsql keyword connection string; only the `Host` (and, for Neon, `Ssl Mode=Require`) differs
- `Jwt__Secret` - a real, unique-per-environment random string; never reused between modes
- `Jwt__Issuer`/`Jwt__Audience` - optional; both modes leave these unset and rely on `JwtOptions`' built-in defaults

**The Windows client** (`src/CCMC.Desktop/appsettings.json`,
`CloudApi:BaseUrl`) always points at `http://localhost:8081/` in both
modes - it talks to the locally running `cc-mc` container either way; only
what's *behind* `cc-mc` changes. This was previously left at a stale
`http://localhost:5000/` (the old pre-Docker `dotnet run` default), which
was the root cause of the client always falling back to offline mode -
see STATUS.md "Application Bug Fixes" for the full diagnosis.

**Never commit** a filled-in `.env`, `.env.docker`, or `.env.neon` file,
and never paste a real connection string or secret into this README, any
other tracked file, or a commit message.

**Neon PostgreSQL** is now the production database target (project
`fancy-cherry-25725711`, branch `production`) - set up via the Neon CLI
(`neon link`, `neon config init` + `neon.ts`, `neon deploy`). Verified:
all three current migrations apply cleanly to a fresh Neon database,
`/health` and `/health/db` both return healthy against it, and -
correctly - `ASPNETCORE_ENVIRONMENT=Production` does **not** create the
`DevelopmentSeeder`'s demo accounts there. **Update (2026-09-15):** Neon
`production` is no longer empty - a dedicated `ProductionBootstrapSeeder`
(env-var-gated via `Bootstrap:AdminEmail`, a no-op unless configured,
idempotent, never overwrites an existing password) was used to create one
Admin, one Manager, one Operator, and one Chilling Centre, verified
directly against Neon (correct role/permission counts, correct centre
scoping, real PBKDF2 password hashes). See HOW_TO_RUN.md §9 and
STATUS.md "Neon Production Bootstrap (2026-09-15)" for the full detail
and how to bootstrap a further account/centre later.

**Next step: deploy this same `cc-mc-api` image to Render**, pointing it
at Neon via the same `ConnectionStrings__CcmcDb` environment variable,
using Render's own environment-variable UI (not this repo's
`.env.production` file, which stays local-only). **Currently BLOCKED**
(2026-09-15): the GitHub repository is owned/controlled by the CEO, and
the operator working on this repo does not have the access needed to
connect the private repository to Render - an access/permissions
blocker, not a technical one. See HOW_TO_RUN.md §10.

Migrations continue to run via the existing automatic
`db.Database.Migrate()` at startup (see §29.11) - this was verified
directly against Neon, not just locally.

### 29.17 Known Gaps

- **Manager override has no idempotency key** (§29.6) - a genuine
  network-level retry of an already-applied override cannot be
  distinguished server-side from a stale duplicate request; the client's
  outbox marks it `FAILED` for manual review instead of guessing. Adding
  an idempotency key to this endpoint (mirroring reception creation)
  would close this gap but was out of scope for this pass - the required
  contract change would need corresponding client-side work in
  `CCMC.Infrastructure.Sync`, and was not made without an explicit
  instruction to change the Windows client.
- ~~No production administrative bootstrap mechanism~~ - **Resolved
  2026-09-15.** `DevelopmentSeeder` still correctly never runs outside
  `ASPNETCORE_ENVIRONMENT=Development`, but a separate, env-var-gated
  `ProductionBootstrapSeeder` now exists and was used to create real
  Admin/Manager/Operator accounts and one Chilling Centre in Neon
  `production` - see HOW_TO_RUN.md §9 and STATUS.md "Neon Production
  Bootstrap (2026-09-15)".
- **No in-app password-change/reset endpoint anywhere.** Rotating any
  account's password today (bootstrap-issued or otherwise) requires
  direct database access using the same `IPasswordHasher` the app itself
  uses at runtime - there is no script or endpoint for this yet.
- **No installer/containerization** for the API itself yet - see §29.16.
- **Render deployment is BLOCKED, not merely undone** - the GitHub
  repository is owned/controlled by the CEO, and the operator working on
  this repo does not currently have the access needed to connect the
  private repository to Render. See §29.16 and HOW_TO_RUN.md §10.
