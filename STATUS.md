# CCMC Engineering Progress

> Formerly this repo's root `CLAUDE.md`. Moved here so `CLAUDE.md` could
> become generic, stack-level engineering guidelines (testing, CI, code
> style, git hygiene, security) instead — see the new `CLAUDE.md` at repo
> root. This file remains the detailed, chronological build log: phases
> completed, bugs found and fixed, product decisions made. `context.md`
> is the current-state summary that cites specific sections of this file;
> read this one when you need the *why* and *when* behind a decision
> `context.md` only references.

## Current Objective

Build a native Windows application (C# / .NET 8+ / WPF) for Chilling
Centre Milk Collection that talks directly to RS232 devices (weighing
scale, milk analyser), persists locally in SQLite, and synchronizes to a
cloud API over HTTPS. This replaces the previously-planned Node.js "Local
Device Gateway" + React web architecture, which is now legacy.

**The cloud API is now also built in this repository** (see "CC-MC Cloud
Backend" below) - a fresh ASP.NET Core 8 + EF Core + PostgreSQL
implementation, **not** a port of the old NestJS API (which remains only
on the unrelated `pranav-dev` branch, never checked out/merged/copied
from). The Windows app's `CCMC.Contracts` project is shared verbatim by
both sides, guaranteeing wire-format agreement without duplicated DTOs.

**Status: Windows app - Phases 0–12 foundation complete, a critical fix
pass, and a product-decisions pass.** A read-only pre-commit review found
two BLOCKER-level defects and several warnings in the Phase 4–12
implementation; both blockers were fixed and verified. Several previously
"Decisions Pending" items have since been resolved as explicit product
decisions and implemented: configurable multi-centre device serial
settings, offline operator login (Argon2id + DPAPI), operator-token-only
sync (no service account), hand-rolled SQLite confirmed as the permanent
choice, and manager-override cloud sync via its own durable outbox. See
"Product Decisions" below for exactly what was decided and what remains
genuinely deferred (Videocon/analyser protocol decoders, the installer).
Current state verified: clean build, 89 automated tests passing, a real
publish, and a real launch confirming device-manager initialization via
the log output (see "Tests Completed").

**Status: Cloud backend - built and verified against a real, live
PostgreSQL 16 database.** Auth/RBAC, centre-scoping, master data CRUD,
idempotent reception create, manager override, audit logging, dashboard
summary, health checks, and Swagger are all implemented and manually
curl-verified end-to-end, plus 21 automated integration tests (all
passing) against a dedicated `ccmc_cloud_test` database. Two real runtime
bugs were found and fixed via this live testing (JWT claim remapping;
Npgsql UTC-only `DateTimeOffset` requirement) - see "CC-MC Cloud Backend"
→ "Fixed Bugs" below.

**Update (2026-09-12): Milk Rate Calculation (BRD v5.0 section 25)
implemented** - see "Milk Rate Calculation" below for the full writeup.
Full solution (`dotnet test CCMC.sln`): **187/187 tests passing** (154
Windows-client + 33 cloud, the cloud suite run against a real local
PostgreSQL instance, not skipped). The "110/110" figure previously here
was already stale before this update (it predated the milk-analyser work
below) - see "Milk Rate Calculation" for how the current count was
actually verified.

## Architecture Decisions

- **No web application, no Node gateway** in the target architecture.
  `apps/web`/`apps/gateway` (on the legacy `pranav-dev` branch) were not
  ported - only their proven *design* (SQLite outbox schema, idempotency,
  retry/backoff, auth-token refresh) informed this implementation, which
  is independently written C#, not copied code.
- **The existing NestJS cloud API is the reuse target**, called through
  `CCMC.Infrastructure.Sync.HttpCloudApiClient` against the exact routes
  documented in context.md "API Integration" (unprefixed, e.g. `POST
  reception`) - nothing invented. `pranav-dev` itself was never checked
  out, merged, or modified in this work; the contract was read once
  during Phase 0 (`git show pranav-dev:<path>`, read-only) and is now
  documented in this branch's own files, which is what Contracts/the HTTP
  client are built from.
- **MVVM is explicitly NOT used**, per this session's direct instruction
  (overriding BRD v2 §3/§30, which lists MVVM as the UI pattern) - see
  the prior "Architecture Decisions" entry, unchanged. WPF windows use
  plain code-behind calling into `CCMC.Application` services.
- **Solution structure** (built, matches BRD v2 §3 and the target
  architecture exactly):
  ```
  CCMC.sln
  src/
  ├── CCMC.Desktop/         WPF, code-behind only, DI composition root in App.xaml.cs
  ├── CCMC.Application/     Workflows/services, depends on Domain + Contracts only
  ├── CCMC.Domain/          Entities/enums/value objects/interfaces, zero project references
  ├── CCMC.Infrastructure/  SQLite, serial, device adapters, HTTP client, DI wiring targets
  └── CCMC.Contracts/       Wire DTOs mirroring the cloud API, zero project references
  tests/
  └── CCMC.Tests/           xUnit - Domain, Persistence, Sync, Serial
  ```
- **SQLite access:** hand-rolled `Microsoft.Data.Sqlite` + a small ordered
  migration runner (`SchemaMigrator`), not EF Core. Chosen over EF Core to
  avoid the `dotnet-ef` tool/DbContext design-time machinery for a schema
  this size, while still giving a real, tracked, transactional migration
  history (`schema_migrations` table) - satisfies BRD's "EF Core or
  equivalent." Revisit if the schema grows enough to justify EF Core's
  LINQ/change-tracking.
- **Device protocol boundary is real but intentionally undecoded.** Both
  `VideoconWeighingScaleAdapter` and `GenericMilkAnalyserAdapter` fully
  implement connection lifecycle, one-owner-per-COM-port enforcement, and
  raw-byte capture (`RawCaptureLogger`) - but `ReadWeightAsync`/
  `ReadQualityAsync` always throw `DeviceProtocolNotEstablishedException`
  rather than fabricate a reading, because no verified protocol exists for
  either device. This is the explicit, deliberate "implement the boundary,
  don't fake the decode" outcome this session's instructions required.
- **Windows app supports online-first login with an offline fallback**
  (product decision - see "Product Decisions" below for the full design).
  The sync engine still rides the currently signed-in operator's own
  cloud access token - **no service-account/unattended-sync credential**
  was introduced (explicit product decision, "do not over-engineer").
- **Cloud decimal fields deserialize via a targeted custom `JsonConverter`**
  (`FlexibleDecimalJsonConverter`/`FlexibleNullableDecimalJsonConverter`
  in `CCMC.Contracts.Json`), not a blanket `JsonSerializerOptions.NumberHandling`
  change - see "Fixed in the critical fix pass" (Blocker 1). The cloud's
  TypeORM `decimal` columns serialize as JSON strings; this is the
  narrowest fix that accepts both string and number on read while never
  changing outbound (request) serialization.
- **Structured logging is a small, dependency-free hand-rolled
  `ILoggerProvider`** (`FileLoggerProvider`), not a third-party logging
  package - consistent with the project's existing preference (SQLite
  access, migrations) for boring, minimal-dependency infrastructure over
  adding a NuGet package for something this small.

## Product Decisions

Six previously-open "Decisions Pending" items were resolved as explicit
product decisions and (except the installer, deliberately) implemented in
this pass. Recorded here as DECIDED vs. DEFERRED, per the instruction to
keep the two unambiguous:

**DECIDED and implemented:**

1. **Configurable, multi-centre device serial settings.** The app must
   not be gated around one fixed COM port/baud rate - a different
   chilling centre may run entirely different hardware. `DeviceConfigurationWindow`
   now provides: live COM port enumeration (`SerialPort.GetPortNames()`)
   with a Refresh button, the full standard baud-rate list (110-115200,
   editable for non-standard values), data bits (5/6/7/8), parity
   (None/Even/Odd/Mark/Space), stop bits (1/1.5/2), flow control
   (None/RTS-CTS/XON-XOFF/both), device/vendor name, enabled flag, a real
   **Test Connection** (opens the actual port with the entered, not-yet-saved
   settings; reports `SerialPortOwnershipException` distinctly if the
   port is already owned by an active device), and Save. COM4/2400/8-N-1
   remains encoded exactly once, as data
   (`SerialConfiguration.VerifiedWeighingScaleDefault`), used only as the
   fallback default when no `DeviceConfiguration` row exists yet - never a
   business-logic assumption. Device protocol parsing remains fully
   separate from this serial transport configuration (no protocol
   guessed or implemented here - see "Things We Must NOT Do").
2. **Offline operator login - implemented.** Online-first: a successful
   `POST /auth/login` caches an **Argon2id** verifier (`Konscious.Security.Cryptography`)
   plus an identity/role/permission/centre-access snapshot, both DPAPI-protected
   (`System.Security.Cryptography.ProtectedData`, `CurrentUser` scope) at
   rest in a new `offline_credentials` SQLite table
   (`CCMC.Infrastructure.Auth.OfflineCredentialStore`). The plaintext
   password and the cloud access token are **never** persisted - the
   password is used once, in memory, immediately after a successful
   online login, then discarded; the access token is never treated as an
   offline substitute. When the cloud is unreachable (distinguished from
   a genuine online rejection via `CloudLoginResult.IsNetworkFailure` -
   see `HttpCloudApiClient.LoginAsync`), `AuthenticationService` falls
   back to verifying the entered password against the cached verifier. An
   offline session (`Session.IsOffline`) carries an empty access token and
   is clearly shown in the Dashboard ("OFFLINE MODE (sync paused)"), with
   a "Sign in online" action to re-authenticate and resume sync once
   connectivity returns. A genuine online rejection (bad password,
   disabled account) is never second-guessed by the offline cache - see
   `AuthenticationService.LoginAsync`'s doc comment for the exact
   decision tree.
3. **Background/unattended sync - kept simple, as decided.** No
   `GatewayService`/service-account model was introduced.
   `SyncEngineService` still requires an active, **online** operator
   session (`session.IsOffline == false`) and uses that operator's own
   token - unchanged in spirit from before this pass, now made explicit
   that an offline session is treated identically to "no session" for
   sync purposes.
4. **Hand-rolled SQLite over EF Core - confirmed, permanent.** No
   migration to EF Core was made or is planned; see the existing
   "Architecture Decisions" entry above (unchanged - this pass only
   confirms it as a closed decision, not a still-open question).
5. **Manager override sync - implemented via its own durable outbox,
   not a bypass.** `IReceptionRepository.ApplyOverrideAsync` now
   atomically writes the local transaction update, the override audit
   row, AND a new `override_outbox` row in one SQLite transaction (new
   migration `Migration002OfflineAndOverrideSync`). `SyncEngineService.TickAsync`
   processes eligible override-outbox rows (only once their PARENT
   reception has synced and has a `CloudTransactionId` - enforced by a
   SQL join in `OverrideOutboxRepository.GetEligibleAsync`) via the
   cloud's existing, already-verified `POST /reception/:id/override`
   endpoint (`ICloudApiClient.OverrideReceptionAsync`, reworked to return
   a classified `CloudOverrideResult` instead of throwing).
   **Documented cloud contract gap, not invented around:** this endpoint
   has no idempotency-key mechanism (unlike `POST /reception`), so a
   retried request cannot be distinguished from a genuine second attempt.
   This implementation does not guess at a guarantee the cloud doesn't
   provide - a terminal/4xx response is marked `FAILED` for manual/ops
   review (with a log line noting the ambiguity explicitly), never
   auto-retried past that point and never auto-assumed successful. Only
   genuine network-level/5xx/429 failures are retried with backoff, same
   as reception sync.
6. **Installer direction decided, not built.** WiX MSI is the chosen
   packaging technology (see "Decisions Pending" - still deferred
   deliberately; no scaffolding exists).

**DEFERRED (genuinely unresolved, not touched by this pass):**

- Real Videocon scale protocol decoder (blocked on captures/documentation
  that don't exist).
- Milk analyser protocol decoder (no vendor/protocol confirmed at all).
- WiX MSI installer implementation itself.
- Videocon vs. ESSAE hardware discrepancy (still needs explicit human
  confirmation).
- UI-level `PermissionCodes` gating, Sources/Vehicles create/edit UI -
  unchanged, still not built (see "Known Limitations").

## Completed Work

**Phase 4 (Domain + Contracts):** `CCMC.Domain` - enums, value objects
(`SerialConfiguration` incl. the verified weighing-scale default),
entities (`ChillingCentre`, `Source`, `Vehicle`, `QualityRule`,
`MilkReceptionTransaction`, `TransactionOverride`, `AuditLogEntry`,
`DeviceConfiguration`), device interfaces (`IDevice`/`IWeighingScale`/
`IMilkAnalyser` + typed exceptions), `QualityValidationService` (mirrors
the cloud's never-auto-reject rule), `OutboxRecord`/`Backoff`.
`CCMC.Contracts` - wire enums, `PermissionCodes`, auth/master-data/
reception/dashboard/audit DTOs, all matching the documented cloud
contract exactly (field names, optionality, the `outcome`/conflict shapes).

**Phase 5 (SQLite):** `SqliteConnectionFactory` (WAL + synchronous=FULL +
foreign_keys=ON on every open), `SchemaMigrator` + `Migration001InitialSchema`
(10 tables: chilling_centres, sources, vehicles, quality_rules,
device_configurations, local_transactions, transaction_overrides,
outbox_records, audit_log_entries, schema_migrations). Repositories:
`ReceptionRepository` (atomic transaction+outbox insert, DB-level
idempotency via `UNIQUE(local_idempotency_key)`, same-payload-replay vs.
conflicting-payload-throws), `OutboxRepository` (atomic claim via
conditional `UPDATE`, retry/failed/synced transitions, stale-PROCESSING
recovery), `ChillingCentreRepository`/`SourceRepository`/
`VehicleRepository`/`QualityRuleRepository` (replace-all cache pattern),
`AuditLogRepository`, `DeviceConfigurationRepository`.

**Phase 6 (Serial infrastructure):** `SerialPortMapper` (Domain enums →
`System.IO.Ports`), `SerialPortConnection` (async open/close/read-available
over `SerialPort.BaseStream`), `SerialConnectionManager` (enforces exactly
one owner per COM port, throws `SerialPortOwnershipException` on a second
`Acquire()`), `RawCaptureLogger` (per-device/per-day hex-dump capture log
for future protocol reverse-engineering).

**Phases 7–8 (device adapters):** `SerialDeviceAdapterBase` (shared
connection lifecycle + `StateChanged` event), `VideoconWeighingScaleAdapter`
and `GenericMilkAnalyserAdapter` - both real, connectable, capture raw
bytes, and both deliberately throw `DeviceProtocolNotEstablishedException`
from their read method (see "Architecture Decisions"). `DeviceManager`
(implements `IDeviceManager`, falls back to the verified weighing-scale
default when no `DeviceConfiguration` row exists yet).

**Phase 9 (Reception workflow):** `ReceptionWorkflowService` -
`ReadDevicesAsync` (concurrent scale+analyser reads, never throws, reports
manual-entry-needed per device), `ValidateAndSaveAsync` (resolves quality
rules, runs `QualityValidationService`, generates the idempotency key
exactly once, persists atomically, writes an audit entry), `OverrideAsync`
(HOLD → ACCEPTED/REJECTED, local only - see Known Limitations).

**Phase 10 (Sync + cloud API):** `HttpCloudApiClient` (login, centres,
sources, vehicles, quality-rules, create-reception with idempotency key,
override-reception, dashboard-summary - every route from context.md's
documented contract), `HttpResponseClassifier` (201+outcome →
created/duplicate, 401 → auth-retryable, 429/5xx → retryable, 409 →
conflict, other 4xx → terminal - the exact classification documented in
context.md, with a regression test for the "both created and duplicate
are HTTP 201" trap the legacy design's own report flagged).
`SyncEngineService` - startup stale-PROCESSING recovery, per-tick
eligibility query, atomic claim, backoff-driven retry, terminal handling,
all built on `IOutboxRepository`/`ICloudApiClient` so it is unit-testable
without real HTTP or SQLite.

**Phase 11 (Auth/RBAC):** `AuthenticationService` (real login only,
records LOGIN/LOGOUT audit entries), `InMemorySessionStore`
(process-lifetime only - no persisted/offline session), `PermissionCodes`
mirrored from the cloud for future UX-only gating (not yet wired into any
window's visibility - see Known Limitations).

**Phase 12 (Testing + deployment):**
- `CCMC.Tests` (xUnit, references all four layers): 43 tests, **all
  passing** (`dotnet test` verified) -
  `Domain/QualityValidationServiceTests` (accept/hold boundaries, never-reject,
  multi-failure reasons), `Domain/BackoffTests` (determinism, cap,
  monotonic growth), `Persistence/ReceptionRepositoryTests` (atomic
  create, same-key-same-payload replay, same-key-different-payload
  conflict, restart-survival via a fresh repository instance, override
  application), `Persistence/OutboxRepositoryTests` (full state machine:
  eligible → claim → synced/retry/failed, double-claim guard, stale-
  PROCESSING recovery, pending-count), `Persistence/SchemaMigratorTests`
  (all 10 tables created, idempotent re-run), `Sync/HttpResponseClassifierTests`
  (the 201-created-vs-duplicate regression explicitly), `Serial/
  SerialConnectionManagerTests` (ownership enforcement and release,
  without opening any real COM port - hardware-independent).
- **Deployment verified for real**: `dotnet publish -c Release -r win-x64
  --self-contained false` succeeds; the published `CCMC.Desktop.exe` was
  actually launched on this machine, ran for several seconds without
  crashing, and was confirmed (by inspecting `%LocalAppData%\CCMC\`) to
  have created `ccmc.db` (+ WAL/SHM files) via a real `SchemaMigrator.Migrate()`
  run - i.e., the full composition root (DI container, config loading,
  schema migration, login window) genuinely works end-to-end on this
  machine, not just "it compiled." The smoke-test-generated local data
  was deleted afterward so first real use starts clean.
- **Not done**: no MSI/EXE installer (BRD v2 §21, this session's Phase
  12 "Windows Deployment") - `dotnet publish` output is a folder, not an
  installer experience. No hardware-in-the-loop test (no physical
  Videocon scale/analyser connected to this dev environment). No
  captured-frame parser regression tests (no captures exist - see
  Hardware Verification).

## Fixed in the critical fix pass

A read-only pre-commit review (full findings in that review's own
transcript) found two BLOCKERs and several WARNINGs against the Phase
4–12 work above. All were fixed in this pass, verified, and are described
here so a future session doesn't have to re-derive what changed:

- **BLOCKER 1 - cloud decimal JSON deserialization.** The cloud's
  TypeORM `decimal` columns (quantityKg, fat, snf, temperature, minValue,
  maxValue, capacityKg) serialize as JSON **strings** (e.g. `"45.50"`),
  not JSON numbers - confirmed during Phase 0's entity inspection. The
  Windows app's DTOs deserialize these as C# `decimal` with default
  `System.Text.Json` behavior, which throws on a string-encoded number.
  **Fix:** `CCMC.Contracts.Json.FlexibleDecimalJsonConverter` /
  `FlexibleNullableDecimalJsonConverter` (accepts either a JSON number or
  a JSON string on read; always writes a JSON number, so outbound
  requests are unaffected), applied via `[JsonConverter(...)]` only to
  the specific affected DTO properties (`ReceptionTransactionDto`,
  `QualityRuleDto`, `VehicleDto`) - not a blanket
  `JsonSerializerOptions.NumberHandling` change, which would have also
  loosened every unrelated int/long property's parsing strictness.
  Additionally hardened `HttpCloudApiClient.CreateReceptionAsync` to
  catch `JsonException` and classify it as `Retryable` instead of letting
  it propagate uncaught (which previously left the outbox row stuck in
  `Processing` until the 5-minute stale-recovery sweep, then failing
  identically forever).
- **BLOCKER 2 - `DeviceManager.InitializeAsync()` had zero runtime call
  sites.** `WeighingScale`/`MilkAnalyser` stayed `null` for the entire
  app lifetime, making the whole device-adapter stack unreachable dead
  code despite being individually correct. **Fix:** `App.xaml.cs.OnStartup`
  now calls `IDeviceManager.InitializeAsync()` once, right after schema
  migration, wrapped in its own try/catch so a device-init failure can
  never prevent the login window from appearing. `DeviceManager.InitializeAsync`
  itself was also hardened: the scale and analyser are now initialized in
  independent try/catch blocks, so a problem with one device's
  configuration can never prevent the other from initializing. Verified
  for real: a published, launched instance's log shows `DeviceManager`
  reaching `VideoconWeighingScaleAdapter` construction with the verified
  COM4/2400 default (see the log excerpt under "Tests Completed" /
  Important Commands).
- **WARNING 1 - `HttpCloudApiClient` had zero tests**, which is directly
  why Blocker 1 went undetected. Fixed: `HttpCloudApiClientTests` (17
  tests) exercises the real client against a fake `HttpMessageHandler`
  with representative, real-shaped JSON (string-encoded decimals
  matching the actual cloud entity shape, malformed decimals, every HTTP
  status classification, network failure, and timeout) - no live server
  required.
- **WARNING 2 - no structured logging anywhere.** Fixed: a small,
  dependency-free `CCMC.Infrastructure.Logging.FileLoggerProvider`
  (`ILoggerProvider`) writes to `AppPaths.LogsDirectory`
  (`%LocalAppData%\CCMC\logs\ccmc-yyyyMMdd.log`), registered via
  `services.AddLogging(...)` in `ServiceCollectionExtensions`. Logging
  added to: `App.xaml.cs` (startup/shutdown/unhandled exceptions -
  Application), `DeviceManager`/`SerialDeviceAdapterBase` (connect/
  disconnect/failure - Device/Serial), the two concrete adapters (parse-
  boundary "not decoded" warnings, byte **count** only, never raw content
  - Parser), `ReceptionWorkflowService` (capture/validation/save -
  Reception), `SyncEngineService` (attempt/success/retry/failure -
  Synchronization), `AuthenticationService` (login success/failure/logout
  - Security, logs email as an identifier but never the password or the
  issued access token). Raw device bytes continue to go exclusively to
  the separate `RawCaptureLogger` file, never into the general log.
- **WARNING 3 - reading-source provenance could silently drift.**
  Editing a device-populated field after a successful read previously
  left the saved transaction still reporting `ReadingSource.Device`.
  Fixed: `CCMC.Application.Reception.ReadingProvenanceTracker` (a small,
  independently-testable class - see its doc comment for why "MANUAL the
  moment either weight or quality is edited" is the least-ambiguous
  behavior a single transaction-level `ReadingSource` can represent).
  `ReceptionWindow` now resets the tracker at the start of each capture
  cycle, records device-sourced reads through it, and wires `TextChanged`
  handlers (guarded against the window's own programmatic `.Text` writes)
  that demote the relevant reading to manual the moment an operator
  actually edits it.

## Tests Completed

`dotnet test CCMC.sln` → **89/89 passing**, 0 failures, ~3s. Coverage by
area: domain validation & backoff math, SQLite atomicity/idempotency/
restart-recovery/outbox-state-machine/migrations, HTTP response
classification (incl. the documented 201-dual-outcome trap), full
`HttpCloudApiClient` deserialization against representative real-shaped
JSON (string-encoded decimals, malformed decimals, every HTTP status,
network failure, timeout - now including `OverrideReceptionAsync`'s own
classification, incl. the 400-not-on-hold-is-terminal case), serial port
ownership enforcement, device-manager initialization wiring (proves
`DeviceManager → adapter → serial infrastructure` is reachable without
any physical hardware), reading-source provenance, **offline credential
store** (real Argon2id + real DPAPI round-trip: correct/wrong password,
no-cached-credential, refresh-on-re-save, independent-per-email, and a
direct assertion that the stored blob contains neither the plaintext
password nor role names), and **override-outbox** (eligibility correctly
excludes an override whose parent hasn't synced yet, full claim/synced/
failed/stale-recovery state machine). Not covered yet:
`ReceptionWorkflowService`/`SyncEngineService`/`AuthenticationService`'s
own orchestration logic in isolation (they're thin orchestrators over
now-well-tested interfaces; adding fake `ICloudApiClient`/`IDeviceManager`
implementations to test them directly is reasonable follow-up work), and
no WPF UI automation tests (no UI test framework wired up).

Real end-to-end runtime verification (published `.exe`, actually
launched, log file inspected - not just "it compiled"):
```
2026-09-04T19:24:32.2643014+00:00  Information  CCMC.Desktop.App                                CCMC starting up.
2026-09-04T19:24:32.5560057+00:00  Information  CCMC.Desktop.App                                Local database schema is up to date (...\ccmc.db).
2026-09-04T19:24:32.5640402+00:00  Information  CCMC.Infrastructure.Devices.DeviceManager        Weighing scale adapter configured: port=COM4 baud=2400 (source=verified default)
2026-09-04T19:24:32.5648358+00:00  Information  CCMC.Infrastructure.Devices.DeviceManager        No milk analyser configuration found - analyser reads will require manual entry.
2026-09-04T19:24:33.1231899+00:00  Information  CCMC.Desktop.App                                CCMC startup complete.
```
This is the direct, real-world proof Blocker 2 is fixed: `DeviceManager`
is reached, loads configuration, and constructs the Videocon adapter with
the verified default - not merely "the interface exists."

## Hardware Verification

- **Weighing scale (verified, current):** COM4, 2400 baud, 8 data bits,
  no parity, 1 stop bit, no flow control. Manufacturer: Videocon
  Precision Systems. Model number: unknown. Protocol: **still unknown -
  not captured.** Encoded as `SerialConfiguration.VerifiedWeighingScaleDefault`
  (`CCMC.Domain`) and covered by a regression test
  (`SerialConnectionManagerTests.VerifiedWeighingScaleDefault_MatchesPhysicallyVerifiedConfiguration`)
  so an accidental future edit to this constant is caught immediately.
  `VideoconWeighingScaleAdapter.ReadWeightAsync` connects for real (if a
  device were on COM4, `ConnectAsync` would genuinely open it) and reads
  whatever raw bytes arrive into the capture log, then throws
  `DeviceProtocolNotEstablishedException` - **no weight is ever
  fabricated.**
- **Milk analyser:** still no verified serial configuration at all.
  `GenericMilkAnalyserAdapter` exists structurally but has no default
  configuration - `DeviceManager.MilkAnalyser` stays `null` until an
  operator/admin enters one via the Device Configuration screen.
- **Unresolved discrepancy (unchanged from Phase 0):** legacy
  `docs/gateway-architecture.md` (on `pranav-dev`) records a different
  "CEO-confirmed" configuration - ESSAE equipment, 9600 baud - for a
  generic `SerialTransport`, never a real parser. This was **not**
  reconciled and **not** used anywhere in this implementation; the only
  configuration this codebase encodes is the current Videocon/COM4/2400
  fact. Still needs explicit human confirmation before real protocol work
  starts (see Decisions Pending #1).

## Milk Analyser + Quality Decision Flow (2026-09-12)

The physical Ekomilk Milkana KAM98-2A milk analyser is not available in any
dev environment (same "no hardware" constraint as the Videocon scale before
its own verification pass). What changed this pass is that the project owner
supplied two REAL, observed device outputs directly (not manufacturer
documentation, not a live serial capture) - enough to derive a confident,
documented fixed-width field layout without guessing:

```
(03900830283801210000032404503) -> Fat 3.9  Snf 8.3  Clr 28.4  Water 1.21  Protein 3.24
(02500520171638900000020906585) -> Fat 2.5  Snf 5.2  Clr 17.2  Water 38.9  Protein 2.09
```

**Built:**
- `Kam98A2AAnalyserFrameParser` (`CCMC.Infrastructure.Devices`) - the field
  mapping, fully documented in its own doc comment, with a captured-string
  regression test suite (`Kam98A2AAnalyserFrameParserTests`, 15 cases
  including both real samples verbatim, parentheses/whitespace/CRLF
  tolerance, and malformed-input rejection - never fabricates a reading).
  Two entry points share the exact same field-decoding code: `ParseLatest`
  (byte stream, device path) and `ParsePayload` (a single string, manual-test
  path) - by construction, manual test input cannot diverge from what the
  real device path would decode.
- `EkomilkKam98A2AAnalyserAdapter` (replaces the old `GenericMilkAnalyserAdapter`
  placeholder) - real connection lifecycle via the existing
  `SerialConnectionManager`/`RawCaptureLogger` infrastructure (no second
  serial stack), throws `DeviceParseException` rather than fabricating a
  reading when no complete frame is present. **The physical serial link
  itself (COM port, baud rate, real RS232 traffic) has NOT been exercised
  against the hardware** - only the payload decode is verified against real
  samples. Do not treat this adapter's existence as proof the serial
  connection works.
- `MilkQualityReading.Temperature` is now nullable - the KAM98-2A does not
  measure temperature at all (never fabricated, same principle `Clr` already
  used); Water/Protein go into the existing `OptionalParameters` dictionary
  (no new ad-hoc fields).
- **Explicit human quality decision, auto-accept retained but dormant.**
  `QualityValidationService`'s automatic Fat/Snf/Temperature range check
  (the original "auto-accept" logic) is unchanged and still computed on
  every save - but `ReceptionWorkflowService.ValidateAndSaveAsync` no longer
  uses its output as the final `Status`; a `ReceptionDecision` (`Accept`/
  `Hold`) supplied by the caller does, with the automatic suggestion
  recorded in `Reason` whenever the operator's decision differs from it.
  REJECT does not add a new "create as Rejected" path - the existing
  invariant (`TransactionStatus`'s own doc comment: Rejected only via
  override of a Hold) is preserved exactly; `RejectAtReceptionAsync` saves
  as Hold via the same `ValidateAndSaveAsync`, then immediately applies the
  existing `OverrideAsync` machinery. Both steps are separately audited and
  separately sync-eligible via their own existing outbox mechanisms -
  nothing new invented at the persistence layer. Covered by
  `ReceptionWorkflowServiceTests` (6 cases, real SQLite).
- **Cloud mirrors the same decision, backward-compatibly.**
  `CreateReceptionRequestDto`/`CreateReceptionCommand` gained an *optional*
  `Status` (Accepted/Hold only - a direct Rejected create is rejected with
  400) plus `Clr`/`Water`/`Protein`/`RawAnalyserPayload`. When supplied, it
  is authoritative (the cloud's own automatic suggestion is still computed
  and logged, same dormant treatment as the client); when omitted, the
  cloud falls back to its pre-existing fully-automatic behavior unchanged -
  this is why `Create_ValidReception_Returns201WithCreatedOutcome` and
  `Create_QualityOutOfRange_ReturnsHold_NeverAutoRejects` (both predating
  this pass, neither sending `Status`) still pass unmodified. New EF Core
  migration `20260912023742_AddMilkAnalyserFields` (additive, nullable
  columns only) applied to `ccmc_cloud_dev`. New SQLite migration
  `Migration003AnalyserFields` (same shape) for the Windows client.
- **Reception window UI**: added CLR/Water/Protein/raw-payload fields, a
  clearly-labelled "Milk Analyser - Manual Test Input" panel (raw string ->
  the exact same parser -> the exact same fields - see HOW_TO_RUN.md), and
  three explicit ACCEPT/HOLD/REJECT buttons replacing the old single
  "Validate and Save Reception" button. All prior functionality (centre/
  source/vehicle selection, Read Devices, weight capture, provenance
  tracking) is unchanged.
- Weighing scale integration untouched - confirmed via this session's own
  app log (`DeviceManager` still reaches `VideoconWeighingScaleAdapter` on
  COM4/2400 from the existing `DeviceConfiguration` row).

**Not done / explicitly out of scope this pass:**
- Hardware-in-the-loop verification of the KAM98-2A's actual serial
  connection (device unavailable - see above).
- Extending the configurable quality-rule engine to Clr/Water/Protein - no
  confirmed acceptance thresholds exist for them yet, and inventing some
  would violate "no invented business rules"; they are captured, displayed,
  and available for operator judgment, not (yet) auto range-checked.

## Milk Rate Calculation (2026-09-12)

BRD v5.0 section 25's live Rate/Amount calculation, ported exactly from
the BRD's own spec (itself ported from a legacy Android reference
implementation - `MilkCollectionFragment.getAmount()`/`RateFormulaFragment`,
not part of this repository, so only the BRD's written formula was
available to implement against, not any reusable code).

**Built:**
- `RateCalculationService` (`CCMC.Domain.Services`, client) - pure, static,
  independently unit-tested (16 cases: both modes, rounding, blank-input
  and missing-config zero-fallback, boundary values). Implements both BRD
  modes exactly: Fat-vs-SNF (`Rate = (Value1+Value2)*0.22*(FAT/100) +
  (Value1+Value2)*0.36*(SNF/100) + 0.32`) and TS-based (`Rate =
  (FAT+SNF)*TsRate/100`), `Amount = Rate * Weight`, both rounded to 2
  decimals with `MidpointRounding.AwayFromZero` applied independently to
  Rate and to Amount (the BRD's own section ordering - formula, then
  Amount = Rate x Weight, then a separate 25.4 "Output Formatting" step -
  read literally, not as "round Rate first, then multiply").
- `RateFormulaSettings` (`RateType`, `Value1?`, `Value2?`, `TsRate?`,
  nullable `CentreId`) - a new cloud-owned master-data concept, mirroring
  `QualityRule`'s exact centre-specific-over-global resolution. **No
  numeric default was seeded anywhere** - the BRD gives no example
  Value1/Value2/TsRate (unlike section 10's FAT/SNF/Temperature limits),
  so inventing one would have violated "no fabricated business values."
  A centre's Rate/Amount is genuinely 0/0 until a Manager/Admin configures
  it via the new `PUT /rate-formula-settings` endpoint (`RATE_FORMULA_VIEW`/
  `RATE_FORMULA_CONFIGURE` permissions, granted to Operator(view)/
  Manager+Admin(view+configure) in `DevelopmentSeeder`, same pattern as
  `QUALITY_RULE_*`).
- Synced client-side via the existing `MasterDataSyncService` pattern
  (`GET /rate-formula-settings` -> local SQLite cache) - rate calculation
  works fully offline once a centre's formula has been pulled at least once.
- `ReceptionWorkflowService.ValidateAndSaveAsync` resolves the centre's
  rate formula settings and computes Rate/Amount at the exact moment of
  ACCEPT/HOLD (capture time), storing them on the transaction - never
  recomputed later. A new public `CalculateRateAsync` method is the single
  shared resolution+calculation path also called by the reception UI's
  live preview, so the displayed value is structurally guaranteed to match
  what gets persisted, not just by convention.
- `ReceptionWindow` gained a "RATE & AMOUNT" card that recalculates live
  as Weight/FAT/SNF change (including when a device read or the manual
  analyser test populates those fields programmatically), always
  re-resolving the freshest locally cached configuration rather than a
  value cached at window-open time.
- `Rate`/`Amount` added as **nullable** `decimal?` (not `required`) on
  `MilkReceptionTransaction`, both client and cloud - the same reasoning
  as `Clr`/`Water`/`Protein`: a reception saved before this feature
  existed has `NULL`, distinct from a reception where the calculation
  legitimately produced `0.00` (BRD's own "not configured" rule). Client:
  `Migration004RateCalculation` (additive `ALTER TABLE`, SQLite). Cloud:
  EF Core migration `AddRateFormulaCalculation` (`numeric(10,2)`/
  `numeric(14,2)`, additive, nullable) - generated with `dotnet ef
  migrations add` and applied for real against a live local PostgreSQL 16
  instance during this work (not just written and left unverified).
- **Cloud trust model matches Clr/Water/Protein exactly**: the cloud does
  not recompute Rate/Amount - it stores whatever the client computed and
  sent, verbatim. This is what makes the BRD's own invariant ("a
  transaction retains the Rate/Amount calculated at collection time even
  if configuration later changes") hold structurally, confirmed directly
  (not just asserted) both in `ReceptionWorkflowServiceTests` and in a
  live end-to-end run: a reception's Rate/Amount were unchanged after the
  centre's rate formula was reconfigured to a different mode with
  different numbers.

**Verification performed** (see also `context.md`'s test-count update):
187/187 tests passing (154 client incl. `RateCalculationServiceTests` and
rate-specific additions to `ReceptionWorkflowServiceTests`/
`ReceptionRepositoryTests`/`SchemaMigratorTests`/a new
`RateFormulaSettingsRepositoryTests`; 33 cloud incl. a new
`RateFormulaSettingsTests` and rate-specific additions to
`ReceptionTests`), the cloud suite run against a real, freshly initialized
local PostgreSQL 16 instance (a throwaway `pg_ctl`-managed data directory,
not the machine's own stopped `postgresql-x64-16` service, which was left
untouched). Additionally, a real end-to-end harness (a throwaway console
program outside this repository) exercised the exact production
`ReceptionWorkflowService`/`MasterDataSyncService`/`SyncEngineService`/
`HttpCloudApiClient` code against the real running Cloud API: configured
both rate modes via the real `PUT /rate-formula-settings` endpoint,
created receptions and confirmed exact expected Rate/Amount for each
mode, confirmed SQLite persistence and outbox creation, synced to the
cloud and confirmed verbatim storage, confirmed no duplication on a
repeated sync tick, confirmed historical immutability under a
configuration change, confirmed an unconfigured centre computes 0/0
rather than fabricating a value, and confirmed a reception created while
the cloud was genuinely unreachable still saves locally and syncs
successfully once reachability is restored, without duplication.

**Not done / explicitly out of scope this pass:**
- A literal mouse-click walkthrough of the WPF `ReceptionWindow` - no GUI
  automation tool was available for a native Windows/WPF app in this
  environment. The verification above exercises the identical underlying
  service code the UI calls (the strongest verification available without
  that tooling), but the specific claim "the on-screen Rate/Amount labels
  visibly update on keystroke" rests on code inspection of the
  `TextChanged` wiring, not an observed screenshot.
- A WPF UI for **configuring** the rate formula (Rate Type/Value1/Value2/
  TsRate) - deliberately not built, following the exact same precedent as
  Sources/Vehicles (the cloud API supports mutation, the Windows client
  only reads/caches; no client-side master-data-edit UI exists anywhere
  in this repo yet). Configuration happens via the cloud API directly
  (`PUT /rate-formula-settings`) until/unless a broader master-data-edit
  UI is built for all three concepts together.
- Source Hierarchy field and the Notification hook (BRD section 17) -
  separate, still-unimplemented MVP gaps, out of scope for this pass.
- A Receipt/Result view showing Rate/Amount to the operator (BRD section
  19's "RECEIPT / REPORT" end state) - the reception screen shows it
  live, but no printable/exportable receipt exists yet.

## Rate/Amount Semantics Verification + Docker (2026-09-12)

**Rate/Amount rounding order, re-verified directly against BRD text
(not from memory of the earlier pass):** BRD v5.0 section 25.2/25.3
define `Rate = <formula>` then `Amount = Rate x Weight` as two lines with
no rounding mentioned; section 25.4 ("Output Formatting") is a separate,
later section stating both are rounded "before being displayed and used
for printing/SMS receipts." Read literally, rounding is output formatting
applied to both already-computed full-precision quantities, not an
intermediate step feeding back into the Amount formula - i.e. Amount uses
the FULL-PRECISION Rate, not the 2-decimal-rounded one. `RateCalculationService`
already implemented it this way; confirmed unchanged, no code correction
needed. A discriminating regression test was added
(`Calculate_TsBased_DiscriminatingCase_UsesFullPrecisionRateForAmount_NotRoundedRate`
in `RateCalculationServiceTests.cs`): FAT=3.00, SNF=4.00, TsRate=15.05,
Weight=45.5 -> raw Rate 1.0535 (displayed/stored rounded to 1.05), Amount
from the raw rate = 47.93, Amount if the rounded rate had been used
instead = 47.78 - genuinely different 2-decimal results, proving which
code path actually runs rather than merely documenting an assumption.

**Real bug found and fixed via Docker testing** (exactly what this pass
was for): `JwtOptions.Issuer`/`Audience` were `required string` with no
default, while `Program.cs`'s token-*validation* setup had its own
separate hardcoded fallback ("ccmc-cloud-api"/"ccmc-windows-client"). A
container started with only `Jwt__Secret` supplied (a very plausible real
deployment shape, and exactly what `.dockerignore` now forces on any
image build by excluding `appsettings.Development.json`, which is the
only place Issuer/Audience were previously set) issued tokens with an
**empty** `aud`/`iss` claim, so every authenticated request after a
successful login failed with 401 ("The audience 'empty' is invalid") -
reproduced directly against a real running container before being
diagnosed. Fixed by giving `JwtOptions.Issuer`/`Audience` the same default
constants (`JwtOptions.DefaultIssuer`/`DefaultAudience`) Program.cs already
assumed, and - to close the underlying class of bug, not just today's
symptom - refactoring `Program.cs` so both token issuance
(`JwtTokenGenerator`) and validation (`AddJwtBearer`'s
`TokenValidationParameters`) now resolve Secret/Issuer/Audience from the
exact same `IOptions<JwtOptions>` instance (via
`AddOptions<JwtBearerOptions>().Configure<IOptions<JwtOptions>>(...)`)
instead of two independent `builder.Configuration[...]` reads that could
silently disagree. Locked down by two new tests
(`JwtConfigurationTests.cs`, using a dedicated `SecretOnlyJwtApiFactory`
that supplies only `Jwt:Secret`, mirroring the real container repro
exactly) - both pass now.

**Docker**: `Dockerfile` (repo root, multi-stage: `dotnet/sdk:8.0` build ->
`dotnet/aspnet:8.0` runtime) and `.dockerignore` added for `CCMC.Cloud.Api`.
Built dependency graph verified directly from the `.csproj` files (not
assumed) - only `CCMC.Contracts` + the four `CCMC.Cloud.*` projects are
needed; the Windows client projects and `tests/` are excluded entirely.
`appsettings.Development.json` is excluded from the build context (never
enters the image, in any environment) - confirmed by inspecting the built
image's `/app` contents directly. `Program.cs` also now honors a `PORT`
environment variable (Render's convention) by calling
`builder.WebHost.UseUrls` when present, falling back unchanged otherwise.

Verified end-to-end against a real container (`cc-mc`, on a dedicated
`cc-mc-network`) + a real disposable PostgreSQL 16 container
(`cc-mc-postgres`, not the machine's own `postgresql-x64-16` service,
left untouched): container starts, binds `http://[::]:8080` (all
interfaces, not localhost-only), `/health` and `/health/db` both return
200, EF Core migrations apply for real (`__EFMigrationsHistory` contains
all three current migrations including `AddRateFormulaCalculation`),
login issues a correctly-signed token with only `Jwt__Secret` supplied,
an authenticated `GET /centres` succeeds, a reception's Rate/Amount
persist through the full HTTP -> Cloud.Application -> EF Core ->
PostgreSQL path exactly as sent (confirmed both via the API response and
directly via `psql`), the cloud does not recompute Rate/Amount (proven by
posting deliberately-inconsistent values and confirming they're stored
verbatim, not corrected), and the container exits cleanly on `docker stop`
(SIGTERM, exit code 0, "Application is shutting down..." logged). Full
solution: 190/190 tests passing (155 client + 35 cloud, the cloud suite
run against a real reachable PostgreSQL).

**Not done this pass, by explicit instruction**: no deployment to Render,
no Neon database created/configured - this was local Docker verification
only, in preparation for that next step.

## Known Limitations

- **No installer.** `dotnet publish` produces a deployable folder, not an
  MSI/EXE setup experience. WiX MSI is the chosen direction (see "Product
  Decisions") but not built. Framework-dependent publish was used (not
  self-contained) - the target machine needs the .NET 8 Desktop Runtime
  installed (already present on this dev machine; unverified on an actual
  chilling-centre PC).
- **No unattended/background-service sync** - by explicit product
  decision (see "Product Decisions"), not a gap. Sync always requires an
  active, online operator session and ticks on a 30-second
  `DispatcherTimer` while `MainWindow` is open.
- **The override cloud contract has no idempotency-key mechanism**
  (unlike reception creation) - see "Product Decisions" #5. A terminal
  4xx response on an override retry is marked FAILED for manual review,
  not auto-resolved, since a false "assumed successful" would be a worse
  failure mode than requiring a human to check.
- **`PermissionCodes` are not yet enforced/checked anywhere in the UI.**
  Every signed-in operator currently sees every button in `MainWindow`
  regardless of their actual cloud-side permissions. The cloud API remains
  the real enforcement point (nothing here is a security gap versus the
  cloud), but the BRD's expectation of UX-level hiding is not implemented.
- **Sources/Vehicles screens are read-only.** The cloud already exposes
  `POST`/`PATCH` for both, but no create/edit UI or local-write-then-sync
  path was built for master data - only reception (and now override)
  create/sync is fully wired end-to-end.
- **Device configuration changes require an app restart** to take effect
  (`DeviceManager` builds its adapters once at startup) - documented
  directly in `DeviceConfigurationWindow`'s save confirmation message.
- **Offline login requires a prior successful online login on the same
  machine, under the same Windows user account** (DPAPI `CurrentUser`
  scope) - there is no way to provision offline access for an account
  that has never logged in online here.
- The KAM98-2A analyser's payload decode is now derived and verified against
  two real observed samples (see "Milk Analyser + Quality Decision Flow"
  above), but its physical serial connection is still unverified - no
  hardware-in-the-loop test exists. The Videocon/ESSAE scale discrepancy
  separately remains open (see Hardware Verification).
- **Decimal precision relies on SQLite `REAL` (double) columns**, not an
  exact fixed-point type. Empirically verified safe for this domain's
  realistic value ranges (a scratch test round-tripped `4.53`, `9.87`,
  `100.29`, `0.1` etc. through `Microsoft.Data.Sqlite` exactly), but this
  is an implicit assumption about value ranges, not a guarantee - would
  need revisiting (e.g. `TEXT`-stored exact decimals) if the schema is
  ever asked to carry much higher-precision values.
- `ReceptionWorkflowService`/`SyncEngineService`/`AuthenticationService`'s
  own orchestration logic has no dedicated unit tests yet (see "Tests
  Completed").

## Known Issues

None currently open against the code that exists - `dotnet build` and
`dotnet test` are both clean, and the two BLOCKER-level defects found by
the pre-commit review (cloud decimal deserialization, device manager
never initialized) are fixed and verified - see "Fixed in the critical
fix pass". See Known Limitations above for remaining scope gaps (not
bugs).

## Decisions Pending

Only genuinely unresolved items remain here - resolved items moved to
"Product Decisions":

1. **Videocon vs. ESSAE hardware discrepancy** - needs explicit
   confirmation of which device/settings are current before any real
   scale protocol work starts.
2. **Installer implementation** - WiX MSI is the chosen technology (see
   "Product Decisions" #6), but the actual packaging project (install
   location, shortcuts, .NET Desktop Runtime prerequisite check, upgrade/
   uninstall behavior) is not built.
3. **Milk analyser vendor/protocol** - completely unconfirmed; no serial
   defaults exist for it at all (unlike the scale's COM4/2400 default).

## Next Steps

1. Obtain real Videocon scale captures (physical device, COM4/2400/8-N-1)
   to unblock Phase 7's actual protocol decoder - see the Parser
   Development Workflow in BRD v2 §26.
2. Resolve the Videocon/ESSAE discrepancy with the business before
   building further on top of either.
3. Design and build the WiX MSI installer project.
4. Wire `PermissionCodes` into `MainWindow`'s button visibility (UX-only,
   cloud remains authoritative).
5. Build create/edit UI + sync-back for Sources/Vehicles if the business
   wants that in the Windows app (vs. staying a cloud/admin-only concern).
6. Get real hardware (or at least a serial loopback/simulator) into the
   dev/test loop for actual device-level testing - today's tests are all
   hardware-independent by necessity.
7. Add direct unit tests for `ReceptionWorkflowService`/`SyncEngineService`/
   `AuthenticationService` themselves (fake `ICloudApiClient`/`IDeviceManager`
   implementations), now that the interfaces they depend on are well-tested.

## CC-MC Cloud Backend

Full detail (architecture diagram, endpoint table, exact commands) is in
`README.md` §29 - this section is the engineering-log version: what was
built, why, and what was found along the way, so a future session doesn't
have to re-derive it.

**Scope of this pass:** implement a completely fresh cloud backend
(ASP.NET Core 8 + EF Core + PostgreSQL/Npgsql) under `src/CCMC.Cloud.*` +
`tests/CCMC.Cloud.Api.Tests`, built around the API contract the Windows
client already expects (read from `CCMC.Contracts`, `CCMC.Infrastructure.Sync`,
`CCMC.Application`, `CCMC.Desktop` - never guessed). Explicitly forbidden:
reusing, porting, or calling into the old NestJS API / Node gateway /
React web app on `pranav-dev`.

**Architecture decisions:**

- `CCMC.Cloud.Domain/Application/Infrastructure/Api` (not bare
  `CCMC.Domain/...`) - avoids an assembly-name collision with the
  pre-existing Windows-client projects now that both live in one solution.
- `CCMC.Cloud.Application` → `CCMC.Cloud.Infrastructure` (flipped from the
  Windows client's own Infrastructure→Application-via-interfaces
  direction) - lets Application services use the concrete `CcmcDbContext`
  directly; a repository-interface layer would have been unused
  abstraction for a server that already owns its one persistence
  technology.
- `CCMC.Contracts` (the Windows client's own wire-DTO project) is
  referenced directly by the cloud side and used as-is for every request/
  response body - guarantees wire-format agreement with zero duplicate
  DTOs to drift.
- Server-side password hashing uses ASP.NET Core Identity's
  `PasswordHasher<T>` (PBKDF2) - a **different** choice from the Windows
  client's own Argon2id+DPAPI (`OfflineCredentialStore`), because the two
  solve different problems: the server hashes a password for verification
  against network requests; the client encrypts a verifier for at-rest,
  single-machine offline login. Not an inconsistency - a deliberate,
  documented difference.
- Idempotency is enforced at the **database** level (a named unique
  index + catching the specific `PostgresException`/`ConstraintName`),
  not application-level check-then-insert - see README §29.6 for the
  full mechanism.
- All monetary/quality-measurement columns use PostgreSQL
  `NUMERIC(p,s)` - **never** float/double. Precisions: `QuantityKg`
  `NUMERIC(10,2)`, `Fat`/`Snf`/`Temperature` `NUMERIC(5,2)`,
  `Vehicle.CapacityKg` `NUMERIC(10,2)`, `QualityRule.MinValue/MaxValue`
  `NUMERIC(6,2)`.
- Real EF Core code-first migrations (`Persistence/Migrations/`), applied
  via `db.Database.Migrate()` at startup in every environment -
  `EnsureCreated()` is never used.
- Custom flat JSON error shape (`{message, conflictingFields?,
  existingTransactionId?}`), not RFC7807 ProblemDetails - chosen because
  the Windows client's `HttpCloudApiClient` already parses error bodies
  in exactly this shape (`ReceptionConflictResponseDto`); ProblemDetails
  would have required either changing the client or an awkward
  translation layer.
- No CORS policy configured, deliberately - the only client is a native
  `HttpClient`-based Windows app, not a browser SPA.

**Fixed bugs** (both found via real end-to-end HTTP testing against a
live PostgreSQL 16 database - compilation alone would not have caught
either):

1. **JWT claim remapping broke all authenticated requests.** ASP.NET
   Core's JWT bearer handler silently renames `sub`/`email` claims to
   legacy XML-namespace URIs by default, so `CurrentUserAccessor`'s
   `JwtRegisteredClaimNames.Sub` lookup found nothing despite a genuinely
   valid, correctly-issued token (`GET /centres` → 401 "No authenticated
   user"; `/sources`/`/vehicles`/`/quality-rules` → 403 with empty
   bodies). **Fix:** `options.MapInboundClaims = false;` in the
   `AddJwtBearer` configuration (`Program.cs`), documented inline.
2. **Npgsql rejects non-UTC `DateTimeOffset` for `timestamptz`.**
   `DashboardService.GetSummaryAsync` built IST-offset (`+5:30`)
   `DateTimeOffset` values for the "today" day-boundary and used them
   directly as EF Core query parameters against `ReceivedAt`
   (`timestamptz`) → `GET /dashboard/summary` returned 500:
   `Cannot write DateTimeOffset with Offset=05:30:00 ... only offset 0
   (UTC) is supported`. **Fix:** convert to UTC
   (`.ToUniversalTime()`) specifically for the database query, while
   still using the IST-offset values to determine which calendar day
   counts as "today" and for the human-readable `DateLabel`.

**Known gap, not a bug:** the manager-override endpoint
(`POST /reception/{id}/override`) has no idempotency-key mechanism (unlike
reception creation) - a genuine network-retry of an already-applied
override cannot be distinguished server-side from a stale duplicate. If
the transaction is no longer `HOLD` when a retry arrives, the request
fails with `409` (`BusinessConflictException`). This matches, and was
verified against, the Windows client's own existing mitigation (its
override outbox already marks a terminal/4xx response `FAILED` for manual
review rather than guessing) - see the Windows-app "Product Decisions" #5
above, which predates and anticipated exactly this gap. Closing it would
require adding an idempotency key to this one endpoint plus corresponding
client-side changes in `CCMC.Infrastructure.Sync` - out of scope for this
pass since it would mean altering the already-shipped Windows client
without an explicit instruction to do so.

**Tests:** `tests/CCMC.Cloud.Api.Tests` (xUnit, 21 tests, all passing) -
`WebApplicationFactory<Program>` running the real startup pipeline (real
migrations, real Development seed) against a dedicated `ccmc_cloud_test`
database (never `ccmc_cloud_dev`). Coverage: health checks (2),
login/JWT/token-validation/email-enumeration-safety/no-password-hash-leak
(6), centre-scoped authorization incl. permission-vs-not-found
distinction (5), reception idempotent-create/duplicate/conflict/
quality-hold/cross-centre-403/cross-reference-400 (8). See README §29.14
for the per-test breakdown.

**Not done in this pass (see README §29.16/§29.17):** containerization/
installer for the API itself; closing the override idempotency-key gap
above.

## Important Commands

```
# Inspect the legacy implementation (NOT on this branch - read-only via git show):
git show pranav-dev:apps/api/src/reception/reception.service.ts
git ls-tree -r pranav-dev --name-only

# Build / test / publish the Windows app (dotnet.exe is not on PATH in
# Bash - use the full path, or: export PATH="/c/Program Files/dotnet:$PATH")
dotnet build CCMC.sln
dotnet test CCMC.sln
dotnet publish src/CCMC.Desktop/CCMC.Desktop.csproj -c Release -r win-x64 --self-contained false -o publish/CCMC.Desktop

# Local app data (SQLite DB, raw device captures, structured logs, in production use):
# %LocalAppData%\CCMC\ccmc.db
# %LocalAppData%\CCMC\captures\
# %LocalAppData%\CCMC\logs\ccmc-yyyyMMdd.log

# Cloud backend - see README.md §29 for full detail
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_dev;"
psql -U postgres -h localhost -c "CREATE DATABASE ccmc_cloud_test;"
dotnet tool install --global dotnet-ef --version 8.0.11
dotnet ef database update --project src/CCMC.Cloud.Infrastructure/CCMC.Cloud.Infrastructure.csproj --startup-project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj
dotnet run --project src/CCMC.Cloud.Api/CCMC.Cloud.Api.csproj --urls http://localhost:5000
dotnet test tests/CCMC.Cloud.Api.Tests/CCMC.Cloud.Api.Tests.csproj
# Swagger: http://localhost:5000/swagger   Health: http://localhost:5000/health , /health/db
```
