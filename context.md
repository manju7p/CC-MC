# CCMC Current Context

## Product

CCMC (Chilling Centre Milk Collection) supports the operational workflow
at a dairy chilling centre: a vehicle arrives with milk from a source, an
operator weighs it (weighing scale) and tests its quality (milk
analyser), the system validates the reading against configured quality
rules, and the result (ACCEPTED / HOLD / REJECTED) becomes a durable
transaction, synced to a central cloud system for reporting/RBAC/audit
across all centres.

**Confirmed tier (v2.1 scope decision — see BRD §20):** CCMC targets the
Milk Chilling Centre (CC/MCC) tier specifically, not the Bulk Milk Cooler
(BMC)/village-collection tier. A Chilling Centre receives already-pooled
milk from multiple upstream BMCs/VLCs (via `Source`/`Vehicle`), not
individual farmer pours — farmer-level payment, per-farmer rate charts
and farmer settlement belong to that upstream BMC/VLC tier and are
explicitly out of scope for this application. See "Scope Decisions
(v2.1)" below for the full reasoning and the vendor evidence behind it.

## Current Architecture

**Target (authoritative, this session):**

```
PHYSICAL DEVICES (RS232: weighing scale, milk analyser)
        │
        ▼
NATIVE WINDOWS APP (C# / .NET 8+ / WPF)
  - local operational workflows
  - device communication/parsing
  - SQLite local database
  - offline operation
  - sync
        │ HTTPS
        ▼
CC-MC CLOUD API (ASP.NET Core 8 / EF Core / Npgsql — built in this repo)
        │
        ▼
PostgreSQL
```

There is **no web application** in the target architecture. The Windows
app is the only operator-facing application. The Windows app **never**
connects directly to PostgreSQL — all cloud communication goes through
the cloud API's HTTP contract.

**Important nuance verified from the repo, updated:** this branch
(`windows-application`) now contains a **complete, fresh cloud API
implementation** (`src/CCMC.Cloud.*`, `tests/CCMC.Cloud.Api.Tests`) built
in a later pass, in addition to the Windows app. It is **not** a port of
the old implementation — the full previous stack (NestJS API + Postgres,
React web, Node gateway) still lives, untouched, on a **separate,
historically-unrelated branch, `pranav-dev`** (different root commit —
`git merge-base windows-application pranav-dev` returns nothing). Nothing
on `pranav-dev` was merged, checked out, or copied into
`windows-application` at any point — the old API's *contract* (routes,
DTO shapes) was inspected once, read-only, during the Windows app's own
Phase 0 and is documented below and in `CCMC.Contracts`; the new cloud
API's actual C# implementation was written independently against that
documented contract, not against the old source.

## Application Responsibilities (Windows app)

- WPF UI: login, dashboard, milk reception, sources, vehicles,
  transactions, device configuration, device testing, sync status, local
  database/history view, audit log view, app settings.
- Device communication: own the COM ports, run the serial pipeline
  (config → connection → raw bytes → frame detection → parser →
  validation → normalized reading), one component owns each port.
- Reception workflow: select source → select vehicle → start reception →
  read devices → review → validate quality → accept/hold/reject → save
  locally → queue sync.
- SQLite persistence: local operational store, source of truth when
  offline.
- Sync engine: push local transactions to the cloud API, idempotently,
  when connectivity allows.
- Local RBAC enforcement is **not** authoritative (mirrors the existing
  system's own rule: "frontend/client checks are UX only, the API is
  always re-verified server-side") — permission checks in the WPF UI hide
  actions the operator's cached role can't do, but the cloud API remains
  the enforcement authority for anything that reaches it.

## Cloud Responsibilities (now implemented in this repo — `src/CCMC.Cloud.*`)

Originally documented from read-only inspection of the old NestJS API
(`apps/api` on `pranav-dev`) during the Windows app's Phase 0; a fresh
ASP.NET Core implementation of this same contract now actually exists in
this repository (see STATUS.md "CC-MC Cloud Backend" and README.md §29 for
full detail - architecture, exact commands, security review). Updated
below to describe what is **actually built and verified**, not just
inspected:

- Authentication: `POST /auth/login` (email+password → JWT + user
  context incl. roles/permissions/centreAccess). **No `GET /auth/me`
  exists** - the Windows client never calls one (confirmed by reading
  `HttpCloudApiClient`), so none was built; session restore is entirely
  client-side (cached login response), which is already how
  `CCMC.Application.Auth` works.
- RBAC: permission-code based (`PermissionCodes.*`, shared via
  `CCMC.Contracts`), enforced by a custom `[RequirePermission(code)]`
  attribute + dynamic `IAuthorizationPolicyProvider` — fails closed if a
  controller action forgets the attribute (falls through to the default
  `[Authorize]` requiring only a valid token, never "no check at all").
- Centre scoping: `CentreAccessGuard.AssertCanAccess` — explicit per-call
  check against the authenticated user's own DB-loaded centre
  assignments, never the client-supplied id alone; a user has either
  `allCentres: true` or an explicit list of `centreIds`.
- Master data: `GET/POST /sources`, `PATCH /sources/{id}`; same shape for
  `/vehicles`; `GET /centres` (no centre create/edit endpoint - out of
  scope, no client need identified).
- Quality rules: `GET /quality-rules`, `PATCH /quality-rules/{id}` (global
  `CentreId: null` or per-centre row). **No `POST /quality-rules`** -
  rules are seed/admin-managed by id in this pass; not a gap the client
  needs closed (it only ever calls `GET`, cached locally for offline
  validation).
- Reception: `GET /reception`, `GET /reception/{id}`, `POST /reception`
  (accepts optional `localIdempotencyKey` — the exact mechanism the
  Windows app's sync engine already relies on, enforced via a real
  PostgreSQL unique index + transaction, not app-level check-then-insert
  — see STATUS.md/README §29.6), `POST /reception/{id}/override` (Manager
  resolves a HOLD to ACCEPTED/REJECTED; requires `RECEPTION_OVERRIDE`).
- Dashboard: `GET /dashboard/summary?centreId=` (IST calendar-day
  boundary, converted to UTC before querying — see the Npgsql bug fixed
  in STATUS.md "CC-MC Cloud Backend").
- Audit: `GET /audit-logs` (server writes audit rows itself on
  login/reception-create/override — the client does not need to submit
  audit events separately).
- **No `/api/v1` prefix** — routes are unprefixed (e.g. `POST /reception`,
  not `POST /api/v1/reception`), matching what `HttpCloudApiClient`
  already calls. BRD v2 §17's `/api/v1/...` examples remain illustrative
  only.
- Base URL / port: ASP.NET Core's dev profile listens on
  `http://localhost:5179`/`https://localhost:7123` by default
  (`launchSettings.json`), or whatever `--urls` is passed. `Jwt:Secret`
  and `ConnectionStrings:CcmcDb` are its own config/secrets — never
  exposed to or needed by the Windows app.
- **No CORS policy** — deliberate; the only client is a native
  `HttpClient`-based Windows app, not a browser SPA.

**Idempotency pattern (now actually implemented, cloud-side, in
`CCMC.Cloud.Application.Reception.ReceptionService`):** when a
`localIdempotencyKey` is supplied, the insert runs inside a transaction; a
real PostgreSQL unique index (`ix_milk_reception_transactions_local_idempotency_key`)
enforces the constraint, and a caught `DbUpdateException` (matching
`PostgresException.SqlState == UniqueViolation` **and** that exact
constraint name) distinguishes a genuine duplicate-key collision from any
other failure - not check-then-insert (avoids the race window):
- row actually inserted → assign transaction number, audit, respond
  `{ ...transaction, outcome: "created" }` (HTTP 201).
- row already existed → compare the incoming payload's business fields
  (centreId/sourceId/vehicleId/quantityKg/fat/snf/temperature - **not**
  operatorUserId) against the existing row: identical → `{ ...existing,
  outcome: "duplicate" }` (HTTP 201, no new audit row); different → `409
  Conflict` naming every conflicting field (a caller bug, not a
  legitimate retry).

This means the Windows app's sync engine can literally reuse this
contract: generate a stable local idempotency key once per captured
reception, retry `POST /reception` with the same key+payload safely
forever, and distinguish `created`/`duplicate` (both success) from a
`409` (real conflict, stop retrying, needs manual attention) — this is
exactly what `HttpResponseClassifier` on the Windows side already does,
and it was verified end-to-end (curl + 8 automated tests in
`ReceptionTests.cs`) against the real, running cloud API, not just
assumed compatible.

## Database Responsibilities

- **PostgreSQL (cloud, authoritative):** schema now owned by this repo's
  own EF Core code-first migrations (`src/CCMC.Cloud.Infrastructure/Persistence/Migrations/`,
  applied via `db.Database.Migrate()` at API startup, never
  `EnsureCreated()`) — **not** the old TypeORM migrations on
  `pranav-dev`, which were never run or reused. Entities (see
  `CcmcDbContext`): `ChillingCentre`, `User`, `Role`, `Permission`,
  `RolePermission`, `UserRole`, `UserCentreAssignment`, `Source`,
  `Vehicle`, `QualityRule`, `MilkReceptionTransaction` (unique index on
  `LocalIdempotencyKey`), `TransactionOverride`, `AuditLog`. All
  monetary/measurement columns are PostgreSQL `NUMERIC(p,s)` — **never**
  float/double: `QuantityKg` `NUMERIC(10,2)`, `Fat`/`Snf`/`Temperature`
  `NUMERIC(5,2)`, `Vehicle.CapacityKg` `NUMERIC(10,2)`,
  `QualityRule.MinValue`/`MaxValue` `NUMERIC(6,2)`.
- **SQLite (Windows app, local operational store):** built (see
  STATUS.md "Completed Work" Phase 5) - a business/domain model logically
  compatible with the cloud shape (mirrors `CreateReceptionRequestDto`),
  plus local-only infrastructure tables (`outbox_records`,
  `device_configurations`, `offline_credentials`). 10 tables total,
  tracked via `SchemaMigrator`/`schema_migrations` - see STATUS.md
  "Completed Work" Phase 5 for the full table list.
- The Windows app **never** holds PostgreSQL credentials and never
  connects to Postgres directly - all cloud access goes through the
  cloud API's HTTP contract, and the cloud API is the only component that
  ever touches PostgreSQL.
- The legacy Node gateway's SQLite design (`docs/gateway-architecture.md`
  §3-§8 on `pranav-dev`) was a strong **design reference** (not code) for
  the Windows app's own outbox/idempotency pattern, already built:
  `local_transactions` + `outbox_records` (1:1), outbox state machine
  `PENDING → PROCESSING → SYNCED`/`FAILED`, atomic creation, startup sweep
  to requeue orphaned `PROCESSING` rows, deterministic exponential
  backoff. See STATUS.md "Completed Work" Phase 5/10 for what was
  actually implemented.

## Device Integration

- Interfaces (per BRD v2 §7/§11 and this session's instructions):
  `IDevice` (`ConnectAsync`, `DisconnectAsync`, `IsConnected`),
  `IWeighingScale : IDevice` (`ReadWeightAsync`), `IMilkAnalyser :
  IDevice` (`ReadQualityAsync`).
- Serial pipeline: Serial Configuration → Serial Connection → Raw Bytes →
  Frame Detection → Device Parser → Validation → Normalized Reading.
  Manufacturer-specific logic must not leak into the reception workflow.
- Exactly one component owns a given physical COM port — never two
  independent readers on the same port.
- **No parser exists for any device yet.** Nothing may be invented; a
  real parser is blocked on controlled protocol captures and/or
  manufacturer documentation (see Open Questions).
- Reading models (BRD v2 §10, minimum fields): `WeightReading` = Value,
  Unit, Stable, Timestamp, DeviceId, RawData. `MilkQualityReading` = FAT,
  SNF, CLR, Temperature, optional parameters, Timestamp, DeviceId,
  RawData.

## Serial Configuration

Configurable per device (`SerialConfiguration`: ComPort, BaudRate,
DataBits, StopBits, Parity, FlowControl, ReadTimeoutMs) — never
hard-coded.

## Current Hardware Facts

- **Weighing scale — verified:** COM4, 2400 baud, 8 data bits, no
  parity, 1 stop bit, no flow control. Manufacturer: Videocon Precision
  Systems. Model: unknown. Protocol: unknown/uncaptured.
- **Milk analyser:** no verified configuration yet — all parameters
  "Configurable" per BRD v2 §5.2, nothing confirmed.
- **Unresolved discrepancy:** the legacy gateway docs
  (`docs/gateway-architecture.md` on `pranav-dev`) record a different,
  earlier "CEO-confirmed" configuration — ESSAE equipment, 9600 baud,
  8/N/1, no flow control — used only to scaffold a generic
  `SerialTransport`, never to build a real parser. This must be
  explicitly reconciled with a human before any real scale adapter is
  attempted; do not assume the newer (Videocon/2400) fact silently
  invalidates the older one, and do not assume the reverse either.

## API Integration

See "Cloud Responsibilities" above for the full current contract. Base
URL is configured via `CCMC.Desktop/appsettings.json`'s `CloudApi:BaseUrl`
- environment-specific values (dev vs. prod cloud endpoint) are an
operator/deployment concern, not hard-coded. For the cloud API built in
this repo (§29 of README.md), point it at wherever that's running, e.g.
`http://localhost:5000/` for a local dev instance started with
`dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj --urls http://localhost:5000`.

**Important, verified wire-format fact:** the cloud's TypeORM `decimal`
columns (`quantityKg`, `fat`, `snf`, `temperature` on reception;
`minValue`/`maxValue` on quality rules; `capacityKg` on vehicles)
serialize as JSON **strings** (e.g. `"45.50"`), not JSON numbers - this
is TypeORM's standard behavior for exact-precision columns. The Windows
app's `CCMC.Contracts` DTOs handle this via
`FlexibleDecimalJsonConverter`/`FlexibleNullableDecimalJsonConverter`
(`CCMC.Contracts.Json`), applied to exactly those properties. This was
originally missed (a critical pre-commit review caught it as a build-
breaking-at-runtime defect - see STATUS.md "Fixed in the critical fix
pass") - if any new DTO property is ever added for another cloud
`decimal` column, it needs the same converter, or it will throw
`JsonException` the first time the cloud actually returns a real,
string-encoded value.

## Authentication / Authorization

- Cloud: `POST /auth/login` → JWT (`JWT_SECRET`-signed, default 8h
  expiry) + `AuthenticatedUser` (id, email, fullName, roles[],
  permissions[], centreAccess). RBAC is backend-authoritative
  (`PermissionGuard` + `CentreAccessService`); nothing client-side is
  trusted as an enforcement point.
- Windows app: **decided and implemented** — each operator logs in with
  their own human credentials via the same `/auth/login` endpoint the old
  web app used, not a shared per-centre service account like the legacy
  `GatewayService` role (that role existed specifically because the old
  gateway was an *unattended* process, not an operator-facing app).
- **Offline authentication — decided and implemented** (BRD v2 §18's
  "explicit security design" requirement). `AuthenticationService` is
  online-first: a successful online login caches an Argon2id password
  verifier + an identity/role/permission/centre-access snapshot via
  `IOfflineCredentialStore` (`CCMC.Infrastructure.Auth.OfflineCredentialStore`),
  DPAPI-protected (`CurrentUser` scope) at rest in a new SQLite table,
  `offline_credentials`. The plaintext password and the cloud access
  token are never persisted. When the cloud is unreachable (distinguished
  from a genuine online rejection via `CloudLoginResult.IsNetworkFailure`),
  login falls back to verifying against the cached credential; the
  resulting `Session.IsOffline = true` session carries an empty access
  token, and `SyncEngineService` treats it identically to "no session" -
  sync stays paused until the operator re-authenticates online (Dashboard
  → "Sign in online"). See `STATUS.md` "Product Decisions" #2 for the
  full design and its explicit constraints (never store the password,
  never use the token as an offline substitute, never let a stale offline
  cache override a genuine online rejection).

## Local-First / Offline Design

Reception flow (BRD v2 §9/§12, this session's instructions): Select
Source → Select Vehicle → Start Reception → Read Devices → Review →
Validate Quality → Accept/Hold/Reject → **Save to SQLite → Create Sync
Record** → Synchronize to Cloud API when available. Local save must
happen before, and independently of, cloud sync — a cloud outage must
never lose a completed local transaction.

## Synchronization Model

- Every local transaction gets a stable, once-generated local idempotency
  key (never regenerated on retry).
- Sync state machine (proven design from the legacy gateway, reusable
  concept): `PENDING → PROCESSING → SYNCED` (terminal, success) or
  `→ FAILED` (terminal, exhausted retries or a genuine 409 conflict from
  the cloud). A stale `PROCESSING` row (crash mid-attempt) is recovered
  back to `PENDING` on next startup.
- Retry uses deterministic exponential backoff, no jitter (the legacy
  gateway's reasoning — single client, low volume — applies equally
  here).
- The cloud's `outcome: "created"` and `outcome: "duplicate"` are both
  success from the sync engine's point of view (both mean "the cloud has
  exactly one row for this key"); only a `409` is a terminal failure.
- **Manager overrides — decided and implemented — obey this same
  architecture**, via their own durable outbox (`override_outbox` table,
  `IOverrideOutboxRepository`), not a bypass. `IReceptionRepository.ApplyOverrideAsync`
  atomically writes the local transaction update, the override audit row,
  and the override_outbox row in one SQLite transaction. An override is
  only eligible for sync once its PARENT reception has already synced and
  has a `CloudTransactionId` (enforced by a SQL join in
  `GetEligibleAsync`), then pushed via the cloud's existing
  `POST /reception/:id/override` (verified in Phase 0, `ICloudApiClient.OverrideReceptionAsync`).
  **Documented contract gap:** this endpoint has no idempotency-key
  mechanism (unlike `POST /reception`) - a retried request cannot be
  distinguished from a genuine second attempt. This implementation does
  not guess at a guarantee the cloud doesn't provide: only network-level/
  5xx/429 responses are retried; any terminal 4xx (e.g. the cloud's own
  "only a transaction currently on HOLD can be overridden" 400 on a
  retry) marks the outbox row FAILED for manual/ops review, never
  auto-retried past that and never auto-assumed successful.

## Repository Structure

Current branch (`windows-application`):
```
Doc/Business Requirements Document.docx    (v1, superseded)
Doc/Business Requirements Document.pdf     (v1, superseded)
Doc/CCMC_BRD_and_Technical_Design_v2.docx  (v2.1 — authoritative source of
                                            truth; §20-23 added this
                                            session, see "Scope Decisions
                                            (v2.1)" below)
CLAUDE.md / STATUS.md / context.md / .gitignore
CCMC.sln
src/
├── CCMC.Domain/          enums, value objects, entities, device interfaces,
│                         QualityValidationService, OutboxRecord/Backoff — zero project references
├── CCMC.Contracts/       wire enums, PermissionCodes, auth/master-data/reception/
│                         dashboard/audit DTOs — zero project references
├── CCMC.Application/     Abstractions (repository/cloud-client/device-manager
│                         interfaces), Reception/Sync/Auth/MasterData services —
│                         refs Domain + Contracts only
├── CCMC.Infrastructure/  Persistence (SQLite conn factory, SchemaMigrator,
│                         repositories incl. OverrideOutboxRepository),
│                         Serial (connection manager, port ownership),
│                         Devices (adapters, DeviceManager), Sync
│                         (HttpCloudApiClient, response classifier), Auth
│                         (session store, OfflineCredentialStore -
│                         Argon2id + DPAPI), Common (clock, key generator),
│                         Logging (FileLoggerProvider) — refs Domain +
│                         Application + Contracts
└── CCMC.Desktop/         WPF, code-behind only, no MVVM. Composition/ (DI
                          wiring incl. structured logging, AppPaths),
                          Windows/ (Login, Main, Reception, ReceptionHistory,
                          Sources, Vehicles, DeviceStatus, DeviceConfiguration
                          - full serial parameter UI, SyncStatus, Settings) —
                          refs all four projects above
tests/
├── CCMC.Tests/              xUnit — Domain, Persistence, Sync, Serial, Devices, Auth (89 tests, all passing)
└── CCMC.Cloud.Api.Tests/    xUnit — real integration tests against a live PostgreSQL test DB (21 tests, all passing)

--- CC-MC Cloud Backend (built in a later pass — see STATUS.md "CC-MC Cloud Backend", README.md §29) ---
├── CCMC.Cloud.Domain/         entities/enums/QualityValidationService — zero project references
├── CCMC.Cloud.Infrastructure/ EF Core (CcmcDbContext, migrations), password hashing, JWT, dev seeder — refs Cloud.Domain
├── CCMC.Cloud.Application/    Auth/RBAC, Reception (idempotent create/override), MasterData, Dashboard, Audit — refs Cloud.Domain + Cloud.Infrastructure + CCMC.Contracts
└── CCMC.Cloud.Api/            ASP.NET Core controllers, JWT bearer auth, permission authorization, Swagger, health checks — refs Cloud.Application + Contracts
```
Confirmed building (`dotnet build CCMC.sln` → 0 warnings, 0 errors) and
confirmed testing (`dotnet test CCMC.sln` → **110/110 passing** — 89
Windows-client + 21 cloud-backend). The Windows app was also confirmed
publishing + actually launching multiple times, including after the
critical fix pass (`dotnet publish` then ran the real `.exe`, which
created a real SQLite DB via `SchemaMigrator` AND logged
`DeviceManager` actually reaching `VideoconWeighingScaleAdapter`
construction - see STATUS.md "Fixed in the critical fix pass" for the
log excerpt; smoke-test artifacts were cleaned up afterward). The cloud
backend was confirmed running for real against a live local PostgreSQL 16
instance (migrations applied, development seed run, every endpoint
curl-verified end-to-end, two real runtime bugs found and fixed - see
STATUS.md "CC-MC Cloud Backend"). See STATUS.md "Completed Work" for the
Windows app's full phase-by-phase breakdown, "Fixed in the critical fix
pass" for its two blockers + three warnings, "CC-MC Cloud Backend" for the
cloud side's own architecture/decisions/bugs-fixed, and "Known
Limitations" for what remains deliberately not built (installer,
Videocon/analyser protocol decoders, override idempotency-key gap).

Legacy branch `pranav-dev` (unrelated history, NOT merged here, NOT
reused by the new cloud backend's implementation) holds the full previous
monorepo: `apps/api` (NestJS cloud API — its *contract* was the reference
point, its *code* was never imported), `apps/web` (React — legacy, not
part of target), `apps/gateway` (Node local gateway — legacy,
design-reference only), `packages/shared-types` (TS DTOs mirroring the
API contract — reference for what `CCMC.Contracts` shape-matches), plus
`docs/architecture.md`, `docs/assumptions.md`,
`docs/gateway-architecture.md`, `docs/gateway-decision.md`.

## Important Existing Components

On `pranav-dev` (historical reference only — read via
`git show pranav-dev:<path>`, nothing ever merged or imported; the new
cloud backend in `src/CCMC.Cloud.*` is an independent implementation, not
a consumer of any of these files):
- `apps/api/src/reception/reception.service.ts` — the idempotent-create
  design the new `CCMC.Cloud.Application.Reception.ReceptionService` was
  independently re-derived to match the same observable contract as.
- `apps/api/src/rbac/*` — permission/centre-scoping model reference.
- `apps/api/src/seed.ts` — the old seed script; **not** read or copied
  when writing `CCMC.Cloud.Infrastructure.Seed.DevelopmentSeeder` (its
  role→permission grants and quality-rule limits are fresh judgment calls
  from BRD v2 directly — see STATUS.md "CC-MC Cloud Backend").
- `packages/shared-types/src/index.ts` — canonical DTO shapes
  (`CreateReceptionRequest`, `ReceptionTransactionDto`,
  `AuthenticatedUser`, `PERMISSIONS`, enums) that `CCMC.Contracts` was
  shape-matched against.
- `docs/gateway-architecture.md` — proven local-storage/outbox/sync
  design reference for the Windows app's `CCMC.Infrastructure` layer
  (already built - see "Completed Work" Phase 5/10).
- `docs/assumptions.md` — every place the BRD was ambiguous and what was
  assumed for the MVP (auto-reject-vs-hold, centre-assignment model,
  transaction numbering, quality-rule scope, etc.) — read before
  reinventing any of these.

## Constraints

- Windows app must work fully offline for a complete reception; internet
  is never required to finish a physical milk reception.
- Windows app must never hold PostgreSQL credentials or connect to
  Postgres directly — only the cloud API (`CCMC.Cloud.Api`) touches
  PostgreSQL.
- Exactly one component owns a given COM port.
- No device protocol may be guessed or invented.
- Sync retries must be idempotent — no duplicate cloud transactions from
  a lost response + retry (enforced server-side by a real PostgreSQL
  unique constraint, not just client discipline).
- UI must not contain business logic; parsers must not contain business
  logic.
- Milk quantities/quality measurements must never use floating-point
  PostgreSQL types — `NUMERIC(p,s)` only (see "Database Responsibilities").
- No secrets (JWT signing key, DB password) committed for any non-dev
  environment — must come from environment variables/a secret store.

## Things We Must NOT Do

- Do not build a web application.
- Do not build/port the Node.js gateway as the primary architecture.
- Do not impose MVVM ceremony (explicit override of BRD v2 §3 — see
  STATUS.md "Architecture Decisions").
- Do not invent a Videocon (or any) device protocol without a real
  capture or manufacturer documentation.
- Do not invent a new cloud API endpoint/field/DB relationship without
  first checking whether the Windows client (`CCMC.Contracts`/
  `HttpCloudApiClient`) actually needs it, or whether the BRD explicitly
  requires it.
- Do not connect the Windows app directly to PostgreSQL.
- Do not reuse, port, or call into the legacy `pranav-dev` NestJS API /
  Node gateway / React web app from the new cloud backend — its contract
  was a reference point, its code was never imported (see "CC-MC Cloud
  Backend" in STATUS.md). Do not mass-delete or ignore `pranav-dev`
  either — it remains historical/reference material.
- Do not add browser-oriented CORS complexity to the cloud API — the only
  client is a native Windows `HttpClient`, not a browser SPA.
- Do not commit or push anything from a session working on this repo
  unless explicitly asked.
- **Do not build chilling-tank/batch tracking, bulk storage tank
  telemetry, CIP, or plant/equipment monitoring** — explicit v2.1 scope
  decision (BRD §22), not an oversight. Checked against six MCC/chilling-
  centre software vendors (Everest, KVR, TecXpert, ProcuPort, Stellapps
  SmartCC, NanoDairy); none of them build this either — see "Scope
  Decisions (v2.1)" below.
- **Do not build farmer-level payment, per-farmer rate charts, or farmer
  settlement** — that belongs to the upstream BMC/VLC tier, not this
  Chilling Centre application (BRD §20).
- **Do not build outbound tanker dispatch or reconciliation/closing-stock
  reporting as part of the MVP** — both are confirmed in-scope (BRD §23)
  but explicitly sequenced Post-MVP; do not pull them into the current
  MVP build without being asked.

## Scope Decisions (v2.1)

This session reviewed the BRD against how privately-owned Milk Chilling
Centres actually operate in India (not Bulk Milk Coolers, and not a
cooperative federation the size of KMF), and against six vendors already
selling software at this exact tier. Three decisions came out of that
review and are now encoded in BRD §20–§23 (v2.1) as well as here:

1. **Confirmed tier: Chilling Centre, not BMC.** See "Product" above.
2. **Out of scope, by decision (BRD §22):** chilling-tank/batch tracking
   (tank assignment, inlet/outlet temperature, chilling start/end, lot
   tracking), bulk storage tank telemetry (level, temperature, age,
   utilisation), CIP (clean-in-place cycle logging), and plant/equipment
   monitoring (chiller, compressor, pumps, tank sensors, power/failure
   events). Rationale: none of six independently-reviewed MCC/chilling-
   centre vendors — Everest Instruments, KVR Technologies, TecXpert, Sort
   String/ProcuPort, Stellapps SmartCC, NanoDairy — publicly build any of
   this either, including Stellapps SmartCC, whose product is explicitly
   a cloud layer for *monitoring* chilling centres. Six independent
   vendors agreeing on the same boundary reads as a market pattern (tank/
   CIP/equipment telemetry sold separately, by plant-automation/SCADA
   firms) rather than a gap unique to this BRD. This device model stays
   scoped to exactly the weighing scale and milk analyser (BRD §5) —
   no additional sensor/telemetry device categories are being added.
3. **Deferred, not descoped (BRD §22, note under §10):** laboratory
   quality tests beyond FAT/SNF/CLR/Temperature — acidity, Clot-on-
   Boiling (COB), antibiotic/adulteration screening — and a lab workflow
   distinct from reception. No verified measurement instrument or
   protocol exists for these yet; revisit once one is identified, no
   fixed date.
4. **In scope but Post-MVP (BRD §23):** outbound tanker dispatch to the
   processing plant, and reconciliation/closing-stock reporting. Both are
   real, vendor-proven capabilities (TecXpert and ProcuPort build both;
   Everest builds dispatch) and require no new device/hardware — pure
   workflow and reporting additions on top of the existing reception +
   sync architecture. Sequenced after the current MVP (BRD §17,
   unchanged) specifically because dispatch depends on reception/sync
   being stable, and reconciliation depends on dispatch data existing.

Full vendor-by-vendor detail (confidence level, what was verified vs.
vendor-claimed, and the per-capability coverage grid) lives in the BRD
§20–§23, not duplicated here — read those sections directly rather than
re-deriving vendor claims from memory.

## Open Questions

Only genuinely unresolved items remain here — several prior open
questions were since resolved as explicit product decisions (see
STATUS.md "Product Decisions") and are no longer listed:

1. Videocon vs. ESSAE hardware discrepancy (see "Current Hardware
   Facts") — needs explicit human confirmation.
2. Milk analyser: no serial configuration or protocol confirmed at all —
   no vendor even named yet, unlike the scale's verified Videocon/COM4/2400.
3. Whether the cloud API's routes (unprefixed, e.g. `POST /reception`)
   will stay stable, or whether an `/api/v1` prefix will be introduced
   before the Windows app ships — assume current unprefixed routes until
   told otherwise.
4. WiX MSI installer implementation — technology chosen, project not built
   (applies to the Windows app; the cloud API also has no containerization/
   deployment packaging yet — see README.md §29.16).
5. The manager-override endpoint's missing idempotency-key mechanism
   (documented gap, both sides already mitigate it — client marks
   retried-after-terminal-4xx as FAILED-for-review, server returns 409 if
   the transaction is no longer HOLD) — closing it properly would need a
   coordinated client+server change, not attempted in either pass without
   an explicit instruction to alter the already-shipped Windows client.

## Current Development State

**Windows app: Phases 0–12 foundation complete, a critical fix pass, and
a product-decisions pass.** .NET 8 SDK (8.0.424) installed and verified.
Full layered implementation exists across all five Windows-app `src/`
projects plus `tests/CCMC.Tests`, and a real published build was launched
multiple times and confirmed — via actual log output, not just process
survival — to (a) initialize its SQLite database correctly and (b) reach
`DeviceManager` → `VideoconWeighingScaleAdapter` construction with the
verified COM4/2400 default.

Two BLOCKER-level defects found by a read-only pre-commit review (cloud
decimal JSON deserialization; `DeviceManager.InitializeAsync()` never
being called) were fixed and verified — see STATUS.md "Fixed in the
critical fix pass". Six further product decisions were then made and
(except the installer, deliberately) implemented — configurable
multi-centre device serial settings, offline operator login (Argon2id +
DPAPI), operator-token-only sync, hand-rolled SQLite confirmed
permanently, manager-override cloud sync via its own outbox, and WiX MSI
chosen as the (unbuilt) installer direction — see STATUS.md "Product
Decisions" for the full detail on each.

**Cloud backend (built in a later pass): fully implemented and verified
against a real, live local PostgreSQL 16 database.** Auth/JWT/RBAC,
centre-scoping, master data CRUD, idempotent reception create, manager
override, audit logging, dashboard summary, health checks, Swagger — all
manually curl-verified end-to-end plus 21 automated integration tests
(all passing) against a dedicated `ccmc_cloud_test` database. Two real
runtime bugs (JWT claim remapping; Npgsql UTC-only `DateTimeOffset`) were
found via this live testing and fixed — see STATUS.md "CC-MC Cloud
Backend" for the full writeup, and README.md §29 for architecture/exact
commands.

**Full solution:** `dotnet build CCMC.sln` → 0 warnings, 0 errors.
`dotnet test CCMC.sln` → **110/110 tests passing** (89 Windows-client + 21
cloud-backend).

What is genuinely NOT done (not oversights — each is either blocked on
external input or an explicit scope cut, see STATUS.md "Known
Limitations", "Decisions Pending", "Product Decisions" - DEFERRED, and
README.md §29.16/§29.17):
- Real Videocon scale / milk analyser protocol decoders (blocked on
  captures/documentation that do not exist yet).
- The WiX MSI installer itself — `dotnet publish` output is a folder;
  the packaging project is not built. No containerization/deployment
  packaging for the cloud API either.
- UX-level permission-based UI hiding in the Windows app (cloud remains
  the real enforcement point either way).
- Sources/Vehicles create/edit UI in the Windows app (the cloud API
  already exposes the endpoints - read-only cache in the client today).
- The manager-override endpoint's idempotency-key gap (documented, both
  sides already mitigate it - not the same as "broken").

Nothing has been committed to git — see `git status` before assuming any
of this is version-controlled yet.
