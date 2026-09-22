# CC-MC — Project Progress

> Analysis date: 2026-09-12 (original audit); **updated 2026-09-12 following implementation of BRD v5.0 §25 Milk Rate Calculation** — see the "Update" callouts throughout, especially §4 and §14. BRD analyzed: `Doc/CCMC_BRD_and_Technical_Design_v2.docx`, **v5.0** ("Rate Calculation Added to MVP"), read in full (both the Business Requirements section §1–§25 and the embedded Technical Design Document §1–§31). Branch analyzed: `windows-application`. The original analysis below was read-only; the Rate Calculation feature was implemented in a separate, later pass (real code, real tests, real end-to-end verification against a live PostgreSQL) — this document was then updated to match, per that task's own documentation-consistency requirement.
>
> Where this document conflicts with `context.md` / `STATUS.md`, this document defers to the BRD for **scope** (per `CLAUDE.md`) but to **direct source-code inspection** for **implementation status** — several claims in `STATUS.md` were found to be stale (see §14 "Stale documentation found" below) and were not taken at face value.
>
> **Update (2026-09-13):** development is paused here as a deliberate checkpoint (documentation-only session, no code changes). See §19 "Checkpoint (2026-09-13)" at the end of this document for what changed since the analysis above, and `architecture.mmd` / `HOW_TO_RUN.md` / `STATUS.md`'s own "Checkpoint (2026-09-13)" section for the full current picture.
>
> **Update (2026-09-15):** Neon production account bootstrap was implemented and verified, and a UI/UX redesign pass landed — see §20 "Checkpoint (2026-09-15)" at the end of this document. Render deployment is now explicitly **BLOCKED** (GitHub repository access controlled by the CEO, not a technical gap) rather than merely "not started."
>
> **Update (2026-09-18):** the WPF client was restructured into a single-window shell (one `MainWindow`, a left sidebar, a swappable right content area), and a Manager/Admin-only **Rate Configuration** screen was added — closing a gap that §4 below did not make obvious: no WPF screen or client method ever existed to configure rates *through the running application* until this date, so every reception's Rate/Amount was actually `0` for the entire period covered by §4's original "🟢 Implemented" claim. See §21 "Checkpoint (2026-09-18)" at the end of this document, and the new root-level `rateconfig.md` for the manager-facing explanation of the feature.

## 1. Current Architecture

As implemented today, matching the BRD's Technical Design section almost exactly:

```
RS232 Devices (Videocon scale, Ekomilk KAM98-2A analyser)
        ↓
CCMC.Desktop (WPF, code-behind — no MVVM)
        ↓
CCMC.Application  (reception workflow, orchestration)
        ↓
CCMC.Domain  (entities, quality rules, provenance)
        ↓
CCMC.Infrastructure  (SQLite via Microsoft.Data.Sqlite, serial I/O, device adapters, HttpCloudApiClient)
        ↓  local SQLite (ccmc.db) — offline-first, always writes here first
        ↓  HTTPS (only when online)
CCMC.Contracts  (shared DTOs)
        ↓
CCMC.Cloud.Api (ASP.NET Core 8, JWT auth, [RequirePermission] RBAC)
        ↓
CCMC.Cloud.Application / CCMC.Cloud.Domain
        ↓
CCMC.Cloud.Infrastructure (EF Core / Npgsql)
        ↓
PostgreSQL
```

The Windows client never connects directly to PostgreSQL — confirmed both in `context.md` and in code (no Npgsql reference in any client-side project). Solution structure (`CCMC.sln`) matches the BRD's §3 Technical Design layer list exactly: `CCMC.Domain`, `CCMC.Contracts`, `CCMC.Application`, `CCMC.Infrastructure`, `CCMC.Desktop`, `tests/CCMC.Tests`, plus the cloud-side `CCMC.Cloud.Api`, `CCMC.Cloud.Domain`, `CCMC.Cloud.Application`, `CCMC.Cloud.Infrastructure`, `tests/CCMC.Cloud.Api.Tests`.

## 2. Overall Status

No single invented percentage is justified — the BRD's eight MVP areas (§17) are unevenly complete. As of the 2026-09-12 update, Rate Calculation has moved from a hard 🔴 to 🟢 (see §4); Source Hierarchy and Notifications remain 🔴. A defensible summary:

| MVP Area (BRD §17) | Status |
|---|---|
| Devices (scale + analyser RS232, config, testing, status) | 🟡 Partial (scale decoded & verified; analyser software-verified only, hardware unverified) |
| Reception (source/vehicle/read/review/validate/accept-hold-reject/save) | 🟢 Complete |
| Local (SQLite, offline, history, audit, sync queue) | 🟢 Complete |
| Cloud (auth, transaction sync, idempotency, sync status) | 🟢 Complete |
| Source Hierarchy (optional parent BMC/route field) | 🔴 Not implemented |
| **Rate Calculation** (BRD §25, live Rate & Amount per collection) | 🟢 **Implemented** (updated 2026-09-12 — see §4) |
| Notifications (outbound-notification hook/abstraction) | 🔴 Not implemented |

**Update (2026-09-12):** Rate Calculation is now fully implemented end-to-end (domain formula, centre-scoped configuration synced from the cloud, live UI recalculation, local persistence, outbox, cloud sync, PostgreSQL storage) and verified with 187/187 tests passing (154 client + 33 cloud, the cloud suite run against a real local PostgreSQL instance) plus a real end-to-end harness against the actual running API. See §4 for full detail. Source Hierarchy and the Notification hook remain unimplemented — they were out of scope for that follow-up task.

**Update (2026-09-18) — important correction to the claim above:** everything this 2026-09-12 update describes as "implemented" (the formula, the configuration entity, the cloud `PUT`/`GET` endpoints, the sync-down of config) really was correct and really was verified — but strictly at the domain/API layer, via a throwaway test harness outside this repository (the update above says so explicitly in its own caveat). **No WPF screen, and no method on `ICloudApiClient`, existed to let an actual Manager call that `PUT` endpoint through the running application** — this was only discovered and closed on 2026-09-18 (see §21). Read literally, "🟢 Implemented" in the table below was accurate about the underlying mechanism and misleading about what an end user could actually do with it. Rate Configuration is now reachable from the app itself; see §4's own "Update (2026-09-18)" note and `rateconfig.md`.

## 3. BRD Requirement Matrix

| Area | BRD Requirement | Current Implementation | Status | Evidence | Gap |
|---|---|---|---|---|---|
| Purpose (§1) | Native Windows app talks directly to devices, local-first, syncs to cloud | Implemented as described | 🟢 | `CCMC.Desktop`→`CCMC.Infrastructure` device adapters; SQLite-first save; `SyncEngineService` | None |
| Architecture (§2) | WPF ↔ SQLite ↔ HTTPS ↔ Cloud API ↔ PostgreSQL, no direct client→Postgres | Matches exactly | 🟢 | No Npgsql reference in any client project | None |
| Device config — Scale (§5.1) | RS232, COM4/2400/8‑N‑1/None/None, configurable | `SerialConfiguration.VerifiedWeighingScaleDefault`; configurable via Device Configuration screen | 🟢 | `SerialConnectionManagerTests.VerifiedWeighingScaleDefault_MatchesPhysicallyVerifiedConfiguration` | Unresolved ESSAE/9600-baud discrepancy noted in a legacy doc on another branch — needs human reconciliation (see §5, §14) |
| Device config — Analyser (§5.2) | RS232, all params configurable | `EkomilkKam98A2AAnalyserAdapter` + serial config screen | 🟡 | `DeviceConfigurationWindow.xaml.cs` | No verified default configuration exists yet (must be entered manually per centre) |
| Device Test (§6) | Test connection → open port → receive → parse → display; UI distinguishes port unavailable / disconnected / connected-no-data / invalid / success | `DeviceStatusWindow` + typed exceptions (`DeviceNotConnectedException`, `DeviceParseException`, `SerialPortOwnershipException`) | 🟢 | `DeviceStatusWindow.xaml.cs`, exception handling in `DeviceConfigurationWindow.xaml.cs:210` | None significant |
| Device Parser Architecture (§7) | Manufacturer protocols isolated from reception workflow | `IWeighingScale`/`IMilkAnalyser` interfaces; adapters return normalized `WeightReading`/`MilkQualityReading` to `ReceptionWorkflowService` | 🟢 | `SerialDeviceAdapterBase`, `IDevice` hierarchy | None |
| Critical Device Rule (§8) | No invented protocol; only real captures or documentation | Followed — scale decoded from real captures; analyser decoded from two real vendor-supplied sample frames, not guessed | 🟢 | `VideoconWeightFrameParser.cs` doc comment; `Kam98A2AAnalyserFrameParser.cs` doc comment | None |
| Milk Reception (§9) | Select Source → Vehicle → Start Reception → Read Devices → Review → Validate → Save | Full pipeline in `ReceptionWorkflowService` + `ReceptionWindow.xaml.cs`, now including live Rate/Amount calculation and display | 🟢 | `ReceptionWorkflowServiceTests.cs` | None significant |
| Transaction & Quality (§10) | FAT 3.0–6.0, SNF 8.0–10.0, Temp 0–10°C; ACCEPTED/HOLD/REJECTED; reasons stored for HOLD/REJECT | `QualityValidationService.Validate` against config-driven `QualityRule` rows (not hardcoded, per Engineering Rule #29.9) | 🟢 | `QualityValidationServiceTests.cs`; reason columns on `local_transactions`/`transaction_overrides` | None — thresholds are configurable data, not hardcoded, which is a stricter interpretation of the BRD than literal hardcoded bounds |
| Local-First Database (§11) | SQLite; Users/Roles/Permissions, Sources/Vehicles, Devices, Reception/Quality/DeviceReadings, QualityRules, AuditLogs, SyncRecords | Real SQLite tables: `chilling_centres, sources, vehicles, quality_rules, device_configurations, local_transactions, transaction_overrides, outbox_records, audit_log_entries, offline_credentials, override_outbox` | 🟡 | Migration001/002, `SchemaMigratorTests.cs` | Table names/shape diverge from the BRD's illustrative list (e.g., no local `Users/Roles` tables — RBAC master data is cloud-owned, cached only via `offline_credentials`; quality fields inline on `local_transactions` rather than a separate `MilkQuality` table). Functionally equivalent, not a literal match — worth a documentation note, not a functional gap |
| Synchronization (§12) | Idempotency key per transaction; retries must not duplicate | `local_idempotency_key` UNIQUE column (client) + `ix_milk_reception_transactions_local_idempotency_key` UNIQUE index (cloud); cloud catches unique-violation and returns existing row rather than duplicating | 🟢 | `ReceptionService.cs:78-183,306-308`; `OutboxRepositoryTests.cs` | Override endpoint specifically has no idempotency-key mechanism yet (documented gap in STATUS.md "Known Limitations") |
| Windows Behaviour (§13) | Run without dev tooling, recover from failures, reconnect devices, operate offline, sync automatically, show device/cloud status | Mostly present | 🟡 | `SyncStatusWindow`, `DeviceManager` reconnect logic | No installer yet (WiX MSI chosen, not built) so "run without developer tooling" isn't yet true for an end user — currently must be run from a built/published folder |
| Security & Audit (§14) | HTTPS/TLS, secure token handling, secure local storage, RBAC, least privilege, audit logging, centre-level authorization, no public device endpoints | Implemented per project's documented split (see §11 of this doc for full detail) | 🟢 | `OfflineCredentialStore` (DPAPI+Argon2id), cloud JWT + `[RequirePermission]` + `CentreAccessGuard`, `AuditLogEntry` writes | RBAC is UX-only on the client (by design, per `CLAUDE.md`); no rate-limiting on cloud login endpoint (minor) |
| Manual Fallback (§15) | Manual entry when device unavailable, marked `ReadingSource = MANUAL` | `ReadingProvenanceTracker` — "MANUAL the moment either field is edited" rule | 🟢 | `ReadingProvenanceTrackerTests.cs` | None |
| Reports (§16) | Daily reception, source-wise, vehicle-wise, quality, device status; export Excel/CSV/PDF | Not found as a dedicated reporting module | 🔴 | `DashboardController.cs` (`GET /dashboard/summary`) exists cloud-side but no client reports UI, no export functionality found | Full gap — no report generation or export anywhere in the repo |
| Rate Calculation (§17, §25) | Live Rate & Amount per collection from FAT/SNF/Weight against configured Rate Formula | **Implemented** (2026-09-12): `RateCalculationService` (both client `CCMC.Domain.Services` and, for config CRUD only, cloud side), `RateFormulaSettings` centre-scoped config synced cloud→client, wired into `ReceptionWorkflowService` and the reception UI | 🟢 | `RateCalculationServiceTests.cs` (16 tests), `ReceptionWorkflowServiceTests.cs` rate-specific tests, `RateFormulaSettingsTests.cs` (cloud), real end-to-end harness against a live PostgreSQL | See full breakdown in §4 |
| Source Hierarchy (§17) | Optional parent BMC/route field on Source | Not found on `Source` entity/DTOs | 🔴 | No `ParentBmc`/`Route` field in `CCMC.Domain` or `CCMC.Contracts` | Full gap |
| Notification Hook (§17) | Outbound-notification hook/abstraction (SMS gateway wiring deferred) | Not found | 🔴 | No `INotificationSender`/similar interface anywhere in the repo | Full gap — not even a dormant abstraction exists yet |
| Acceptance Criteria (§18) | 8 criteria (scale connects, analyser connects, one action reads both, save-before-sync, offline preserved & deduped, quality decisions work, RBAC/audit enforced, device failures don't crash) | 7 of 8 met | 🟡 | See individual rows above | "Milk analyser connects and provides supported quality readings" is met only via manual test input / software decode — the physical serial connection to a real KAM98-2A has never been exercised (see §6) |
| Out-of-scope items (§22) | Chilling-tank tracking, bulk tank telemetry, CIP telemetry, dispatch, reconciliation, calibration alerts, CIP compliance log, plant/equipment monitoring, lab tests beyond FAT/SNF/CLR | None of these exist in the repo | ⚪ | N/A | Correctly out of scope — no accidental scope creep found |

## 4. Milk Rate Calculation

**BRD formula (§25, v5.0 — the newest and highest-priority BRD addition), verbatim from the document:**

- **Inputs**: FAT (%, entered per collection), SNF (%, entered per collection), Weight (litres/kg, entered per collection), Value1 & Value2 (`PREFS_RATE_VALUE1`/`PREFS_RATE_VALUE2`, configured rate-formula settings, used only in Fat-vs-SNF mode), TS Rate (`PREFS_RATE_TS`, configured, used only in TS mode).
- **If FAT, SNF, or Weight is blank → Rate and Amount are both 0, no calculation runs.**
- **Mode 1 — Fat-vs-SNF** (`PREFS_RATE_TYPE = "Fat_vs_SNF"`):
  ```
  Rate = (Value1 + Value2) × 0.22 × (FAT / 100)
       + (Value1 + Value2) × 0.36 × (SNF / 100)
       + 0.32
  Amount = Rate × Weight
  ```
  Requires both Value1 and Value2 configured, else Rate/Amount = 0.
- **Mode 2 — TS-based** (any other `PREFS_RATE_TYPE`):
  ```
  TS = FAT + SNF
  Rate = (TS × TS_Rate) / 100
  Amount = Rate × Weight
  ```
  Requires TS_Rate configured, else Rate/Amount = 0.
- **Output formatting**: both Rate and Amount rounded to 2 decimal places (`##.##`).
- **Explicit scope boundary** (BRD's own words): this is *only* the per-collection Rate/Amount calculation — farmer/pourer ledgers, advances, incentive schemes, and periodic settlement remain out of scope (§20).

**Current implementation: 🟢 IMPLEMENTED (2026-09-12) — confirmed directly against real code and a real running system, not inferred.**

This gap identified in the original audit (below, preserved for the historical record) has since been closed:

> ~~Repo-wide search for `PREFS_RATE`, `Fat_vs_SNF`, `RateFormula`, `RateType`, `getAmount`, `TSRate`/`TS_Rate` across `src/` and `tests/` returns zero matches. No `Rate` or `Amount` field exists on `MilkReceptionTransaction`... This is a full gap, not a partial one.~~

**What was built:**
- **Pure calculation**: `CCMC.Domain.Services.RateCalculationService` (client) implements both modes exactly as specified below, rounding Rate and Amount independently to 2 decimals with `MidpointRounding.AwayFromZero`. 16 dedicated unit tests (`RateCalculationServiceTests.cs`) cover both modes, rounding, blank-input zero-fallback, and missing-config zero-fallback.
- **Configuration**: `RateFormulaSettings` (RateType, Value1?, Value2?, TsRate?, nullable CentreId) is a new cloud-owned master-data concept, mirroring `QualityRule`'s exact centre-specific-over-global resolution pattern. No numeric default was seeded anywhere (the BRD gives no example Value1/Value2/TsRate, unlike §10's FAT/SNF/Temperature limits) — a centre's Rate/Amount is genuinely 0/0 until an authorized user (Manager/Admin, via the new `RATE_FORMULA_CONFIGURE` permission) configures it through `PUT /rate-formula-settings`.
- **Sync**: `GET /rate-formula-settings` is pulled into the local SQLite cache by `MasterDataSyncService`, the same pattern as quality rules — rate calculation works fully offline once a centre's formula has been synced at least once.
- **Reception integration**: `ReceptionWorkflowService.ValidateAndSaveAsync` resolves the centre's rate formula settings and computes Rate/Amount at the exact moment of ACCEPT/HOLD (capture time), storing them on the transaction. `ReceptionWorkflowService.CalculateRateAsync` is a single shared resolution+calculation path also called by the reception UI's live preview, so the displayed value can never diverge from what gets persisted (verified directly, not just by design intent — see the end-to-end harness result below).
- **UI**: `ReceptionWindow` shows a "RATE & AMOUNT" card that recalculates live as Weight/FAT/SNF change (or as device/manual-analyser reads populate those fields), always re-resolving the freshest locally cached configuration rather than a stale in-memory copy.
- **Persistence**: `Rate`/`Amount` added as nullable `decimal?` (not `required`) to `MilkReceptionTransaction` on both client and cloud — nullable specifically so a reception predating this feature is distinguishable (`NULL`) from a reception where the calculation legitimately produced zero (stored as `0.00`, not `NULL`). Client: `Migration004RateCalculation` (SQLite, additive `ALTER TABLE`). Cloud: EF Core migration `AddRateFormulaCalculation` (`numeric(10,2)`/`numeric(14,2)`, additive, nullable) — generated and applied for real against a live PostgreSQL instance during this work, not just written and left unverified.
- **Cloud trust model**: the cloud does **not** recompute Rate/Amount — it stores whatever the client computed and sent, verbatim, the same treatment as Fat/Snf/Clr/Water/Protein. This is what makes BRD invariant "a transaction retains the Rate/Amount calculated at collection time even if configuration later changes" hold structurally, not just by convention — verified directly (see below).
- **Historical immutability**: verified directly, not just asserted — a reception's Rate/Amount were confirmed unchanged after the centre's rate formula was reconfigured to a different mode with different numbers, both in an automated test and in the live end-to-end harness.

**Verification performed:**
- 154/154 client tests passing (up from 126 in the original audit), 33/33 cloud tests passing — the cloud suite run against a real, freshly initialized local PostgreSQL 16 instance (not skipped, not mocked), confirming migrations, the new controller, authorization, idempotency, and Rate/Amount persistence genuinely work end-to-end.
- A real end-to-end harness (a throwaway console program outside the repository, since driving the actual WPF GUI's mouse/keyboard was not possible in this session — see the caveat below) exercised the exact production `ReceptionWorkflowService`/`MasterDataSyncService`/`SyncEngineService`/`HttpCloudApiClient` code against the real running Cloud API and a real PostgreSQL database: logged in as real seeded users, configured Fat-vs-SNF then TS-based rate formulas via the real `PUT /rate-formula-settings` endpoint, created receptions and confirmed the exact expected Rate/Amount for each mode, confirmed SQLite persistence, confirmed outbox creation, synced to the cloud and confirmed the cloud stored the values verbatim, confirmed a second sync tick does not duplicate, confirmed a reception's Rate/Amount survive a later configuration change unchanged, confirmed an unconfigured centre computes 0/0 rather than fabricating a value, and confirmed a reception created while the cloud was genuinely unreachable still saves locally and syncs successfully once reachability is restored, without duplication.
- **Caveat, stated plainly**: this session could not perform a literal mouse-click walkthrough of the WPF `ReceptionWindow` (no GUI automation tool was available for a native Windows/WPF app in this environment). The verification above exercises the identical underlying service code the UI calls, which is the strongest verification available without that tooling — but it is not the same as a human visually confirming the on-screen Rate/Amount labels update correctly. That specific, narrow claim (the UI *label* updates on keystroke) rests on code inspection (the `TextChanged` wiring in `ReceptionWindow.xaml.cs`) rather than an observed screenshot.

**Formula implementation location**: `src/CCMC.Domain/Services/RateCalculationService.cs` (client, authoritative for calculation), `src/CCMC.Domain/Entities/RateFormulaSettings.cs` (client config), `src/CCMC.Cloud.Domain/Entities/MasterData.cs` (cloud config entity), `src/CCMC.Cloud.Application/MasterData/RateFormulaSettingsService.cs` (cloud config CRUD), `src/CCMC.Application/Reception/ReceptionWorkflowService.cs` (integration point), `src/CCMC.Desktop/Windows/ReceptionWindow.xaml(.cs)` (live UI display).

**One documented design decision** (BRD-ambiguous, resolved by literal reading rather than invented): the BRD states the Rate formula, then "Amount = Rate × Weight", then separately (§25.4) that both are rounded to 2 decimals before display/use. This was implemented as: compute full-precision Rate, compute full-precision Amount = Rate × Weight, then round *both* independently to 2 decimals — not "round Rate first, then multiply the rounded Rate by Weight." This is the literal order the BRD's own sections present, not a fabricated business rule, and is called out explicitly in `RateCalculationService`'s doc comment for a human to revisit if the intended behavior was actually the other order.

**Update (2026-09-18):** the calculation/persistence/sync detail above remains accurate and unchanged. What was missing — and has now been added — is the actual Manager-facing configuration path: a **Rate Configuration** screen in the WPF app (`Views/RateConfigurationView`, reached from the main window's left sidebar, Manager/Admin only) and a corresponding `ICloudApiClient.UpdateRateFormulaSettingsAsync` client method that calls the `PUT /rate-formula-settings` endpoint this section already described. Before this addition, that endpoint's only caller was the throwaway harness mentioned above — there was no in-app way to set `Value1`/`Value2`/`TsRate`/`RateType` at all, so every centre's Rate/Amount was `0` in practice. A previously-undiscovered centre-scoping gap in that same endpoint (any Manager/Admin could overwrite *any* centre's configuration, not just their own) was also found and fixed in this pass. Full detail: `STATUS.md` "Visual Correction Pass + Rate Calculation Root-Cause Fix (2026-09-18)"; manager-facing explanation including the exact BRD-sourced meaning of Value 1/Value 2: `rateconfig.md`.

## 5. Weighing Scale (Videocon)

🟢 **Protocol decoded and implemented**, with a caveat about stale documentation (see §14).

- `VideoconWeightFrameParser.cs` decodes a continuous ASCII stream of CRLF-terminated `"[+-]00000.XXX Kg"` frames (13 chars, 3-decimal resolution), only trusting frames with a CRLF on both sides within one read (correctly rejecting frame fragments cut by buffer boundaries) — it never fabricates a reading, returning `null` when no complete frame is present in a given read window.
- `VideoconWeighingScaleAdapter.ReadWeightAsync` calls this parser and throws `DeviceParseException` (not a fabricated value) when a read window doesn't contain a complete frame.
- The code's own doc comments assert this format was "verified via live captures on the physical device (COM4/2400/8-N-1/no flow control), cross-checked against the scale's own front-panel display, and confirmed deterministic across 1000+ captured frames" — this claim lives only in the code comment and commit history (commit `4e31b0c`, "Initial Build -- Scale working", 2026-09-05), not in any checked-in raw-capture artifact (expected — captures are runtime data, not source).
- COM port ownership is enforced at runtime: a second `Acquire()` on the same port throws `SerialPortOwnershipException` (`SerialConnectionManager.cs:22`).
- Serial config: `SerialConfiguration.VerifiedWeighingScaleDefault` = COM4/2400/8‑N‑1/no flow control, covered by its own regression test.
- Tests: `VideoconWeightFrameParserTests.cs`, `SerialConnectionManagerTests.cs`, `DeviceManagerTests.cs` — all passing (see §13).
- **Open item, unresolved in the repo itself**: a legacy document on another branch (`docs/gateway-architecture.md` on `pranav-dev`, per STATUS.md) records a conflicting "ESSAE, 9600 baud" configuration. This was never reconciled and never used anywhere in this implementation — the only configuration this codebase encodes is Videocon/COM4/2400. STATUS.md itself flags this as needing explicit human confirmation before further scale protocol work — carrying that flag forward here unchanged.
- **Distinguish**: "protocol decoded and passes 1000+ regression-style claims in code comments" is not the same as "this specific analyst independently re-verified it against a live physical scale in this session" — no physical Videocon scale is connected to this analysis environment, so this status rests on the repository's own asserted evidence trail (commit message + doc comments + regression tests), which is the strongest evidence available without new hardware access.

## 6. Milk Analyser (Ekomilk KAM98-2A)

🟡 **Software parser verified against real sample data — physical device connection NOT verified.**

- `Kam98A2AAnalyserFrameParser.cs` decodes the 29-digit fixed-width frame. Both BRD-supplied real sample frames are present verbatim as test constants:
  - `(03900830283801210000032404503)` → Fat 3.9, SNF 8.3, CLR 28.4, Water 1.21, Protein 3.24
  - `(02500520171638900000020906585)` → Fat 2.5, SNF 5.2, CLR 17.2, Water 38.9, Protein 2.09
- `Kam98A2AAnalyserFrameParserTests.cs` — 15 test cases including both real samples, parentheses/whitespace/CRLF tolerance, and malformed-input rejection.
- Two entry points share the identical decoding code: `ParseLatest` (byte stream / real device path) and `ParsePayload` (single string / manual-test path) — by construction, manual test input cannot diverge from what the real device path would decode.
- `EkomilkKam98A2AAnalyserAdapter` implements real connection lifecycle via the existing `SerialConnectionManager`/`RawCaptureLogger` infrastructure — the wiring is real, not a stub.
- UI: a "Milk Analyser – Manual Test Input" panel exists on the reception screen (`ReceptionWindow.xaml:123`) feeding the same parser used by the real device path.
- **What is explicitly NOT verified**: the physical serial link (COM port, baud rate, real RS232 traffic against an actual KAM98-2A unit). STATUS.md states this directly: *"The physical Ekomilk Milkana KAM98-2A milk analyser is not available in any dev environment"* and the adapter's own doc comment warns not to treat the adapter's existence as proof the serial connection itself works. This was derived from two real observed device outputs supplied by the project owner, not from a live serial capture or manufacturer documentation — a lower verification tier than the scale's "1000+ live captured frames," and the repo is honest about that difference.
- No default serial configuration exists for the analyser (unlike the scale's `VerifiedWeighingScaleDefault`) — must be entered manually per centre via the Device Configuration screen.

**Distinction for this document, as required**: SOFTWARE PARSER VERIFIED = 🟢 (byte-for-byte match on two known-real samples, with regression tests). PHYSICAL DEVICE INTEGRATION VERIFIED = 🔴 (no hardware-in-the-loop test exists for this device; do not report this as hardware-verified anywhere downstream of this document).

## 7. Reception Workflow

Full trace against BRD §9/§12 and the Technical Design's §12 workflow diagram:

| Stage | Implemented? | Tested? | Persisted? | Synced? | UI? |
|---|---|---|---|---|---|
| Login | 🟢 | 🟢 (`AuthenticationServiceTests.cs`) | Session, not a DB row | N/A | 🟢 `LoginWindow` |
| Select Source | 🟢 | Indirect (workflow tests) | N/A (reference) | N/A | 🟢 (dropdown in `ReceptionWindow`); Sources are read-only in the client UI — no create/edit screen even though the cloud API supports POST/PATCH |
| Select Vehicle | 🟢 | Indirect | N/A | N/A | 🟢 same caveat as Source (read-only client UI) |
| Read Weighing Machine | 🟢 | 🟢 | N/A until save | N/A | 🟢 "Read Devices" button, concurrent read |
| Read Milk Analyser | 🟡 (software path only) | 🟢 (parser) | N/A until save | N/A | 🟢 auto-read + manual fallback panel |
| Combine Readings | 🟢 | 🟢 | N/A | N/A | Handled in `ReceptionWorkflowService.ReadDevicesAsync` |
| Validate Quality | 🟢 | 🟢 | N/A | N/A | Computed, shown in review step |
| Rate/Amount (BRD §17/§25) | 🟢 (updated 2026-09-12) | 🟢 | 🟢 | 🟢 | 🟢 live display in `ReceptionWindow`, computed at ACCEPT/HOLD time |
| ACCEPT / HOLD / REJECT | 🟢 (manual only — see §9) | 🟢 | 🟢 | 🟢 | 🟢 buttons in `ReceptionWindow.xaml.cs:200-204` |
| Save Locally | 🟢 | 🟢 (real temp-file SQLite) | 🟢 atomic w/ outbox record | N/A | Automatic on decision |
| Sync to Cloud | 🟢 | 🟢 (`OutboxRepositoryTests.cs`) | N/A | 🟢 idempotent | `SyncStatusWindow` |
| Receipt / Result | 🔴 | 🔴 | 🔴 | N/A | No receipt/printout screen found |

Note: the BRD's Final Solution Vision (§19) ends with "RECEIPT / REPORT" — no receipt-generation or printing capability was found anywhere in the repo. This remains a real, standalone gap against the BRD's own end-state diagram; Rate/Amount now exist (see §4) and would presumably be shown on such a receipt once it is built.

## 8. ACCEPT / HOLD / REJECT

🟢 **Implemented, manual-only, matches the intended MVP behavior.**

- `QualityValidationService.Validate` checks Fat/Snf/Temperature against config-driven `QualityRule` rows and produces a suggested Accept/Hold outcome.
- `ReceptionWorkflowService.ValidateAndSaveAsync` computes this suggestion but **never uses it as the persisted `Status`** — the persisted status is always the operator's explicit button click (Accept/Hold), with the automatic suggestion recorded only in the `Reason` field when it differs from the operator's actual decision.
- REJECT is not a direct create-time status — it is reachable only via `OverrideAsync` of an existing HOLD, enforced by an `ArgumentException` guard. This "Rejected only via override of a Hold" invariant is deliberate and consistent client- and cloud-side.
- Reasons for HOLD/REJECT are persisted (`local_transactions.reason`, `transaction_overrides.reason`), satisfying BRD §10's requirement.

## 9. Auto-Accept

**Confirmed DORMANT, exactly as intended** — this is not an inference, it is stated directly in the source:

> `ReceptionWorkflowService.ValidateAndSaveAsync` doc comment: *"Dormant automatic suggestion — still computed, still logged, never auto-applied."*

The automatic Fat/Snf/Temperature range check (the original "auto-accept" logic) runs on every save and its output is recorded, but it is never used to set the transaction's actual `Status`. Only an explicit human ACCEPT/HOLD click (or a subsequent REJECT override of a HOLD) sets the persisted decision. The cloud side mirrors this: the automatic suggestion is still computed/logged there too, but an operator-supplied `Status` on create is authoritative when present.

## 10. Offline + Synchronization

🟢 **Substantively implemented, genuinely offline-first, not merely "SQLite exists."**

- Every physical reception is written to local SQLite *before* any cloud interaction — the workflow does not require connectivity to complete (BRD §11's explicit requirement).
- Idempotency: `local_idempotency_key` UNIQUE column client-side; the cloud independently enforces a UNIQUE index and catches the resulting `DbUpdateException` on a Postgres unique-violation (not a check-then-insert race), returning the existing row rather than duplicating on retry (`ReceptionService.cs:78-183,306-308`) — this is a genuinely race-safe idempotency design, not a naive one.
- Sync state machine matches BRD §15 exactly: `outbox_records.status` CHECK constrained to `Pending/Processing/Synced/Failed`, with a `next_attempt_at` column for backoff (RETRY_WAIT is represented as a Pending row with a future `next_attempt_at`, not a distinct enum value — functionally equivalent to the BRD's diagram).
- `SyncEngineService.RecoverAtStartupAsync` requeues orphaned in-flight rows after an unclean shutdown.
- Override sync uses a durable outbox as well (`override_outbox`), reusing the same idempotent-create pattern as reception — but the override endpoint itself has **no idempotency-key mechanism** (a documented, real gap — a lost-response-then-retry on an override could theoretically double-apply, unlike reception create which is protected).
- Offline authentication: a prior online login is required once per Windows user account (DPAPI is `CurrentUser`-scoped) before offline login works for that account — a real, documented constraint, not a hidden bug.
- Conflict handling: none needed/observed beyond idempotent-create semantics — there is no multi-writer conflict scenario in the current design (each transaction is created once, by one operator, at one centre).

## 11. Authentication / Authorization

🟢 **Complete for MVP scope, on both sides, with different (correct) mechanisms per tier:**

- **Client**: `OfflineCredentialStore` — Argon2id password verifier (via Konscious.Security.Cryptography) + Windows DPAPI (`CurrentUser` scope) for the encrypted local blob. Never persists the plaintext password or a cloud access token as an offline-login substitute. `AuthenticationServiceTests.cs`, `OfflineCredentialStoreTests.cs` cover correct/wrong password, no-cached-credential, refresh-on-re-save, independent-per-email, and directly assert the stored blob contains neither plaintext password nor role names.
- **Cloud**: JWT via `Microsoft.AspNetCore.Authentication.JwtBearer`; secret is required from configuration and the app throws at startup (fail-fast) if `Jwt:Secret` is missing in any non-Development environment — never hardcoded. Password hashing uses ASP.NET Core Identity's `PasswordHasher<T>` (PBKDF2) — a deliberately different scheme from the client's Argon2id, appropriate to the different threat models (server-side vs. offline-cached credential), not an inconsistency.
- **Authorization/RBAC**: `[RequirePermission(code)]` + a dynamic `PermissionPolicyProvider`/`PermissionAuthorizationHandler`, enforced server-side on essentially every mutating/sensitive endpoint. `CentreAccessGuard`-equivalent (`RequestUser.AssertCanAccess`) enforces centre-scoping in `ReceptionService`. One nuance: an endpoint with `[Authorize]` but no `[RequirePermission]` defaults to "any authenticated user may call it," not an automatic deny — this is a documented design choice in the code, and every current endpoint is correctly annotated, but it is not a true global fail-closed default for a future endpoint someone forgets to annotate.
- Client-side RBAC is UX-only (every operator currently sees every reception button regardless of `PermissionCodes` — documented as a known limitation), which is explicitly acceptable per this project's own security convention: **the client's own permission checks are UX-only and the server enforcement is the actual control**, and that server-side enforcement is real.

## 12. Cloud Backend

- **API**: no `/api/v1` prefix (deliberate, per code comments matching the actual client contract — the BRD's endpoint list was explicitly "architectural examples only"). Actual routes: `POST /auth/login`, `GET/POST /reception`, `GET /reception/{id}`, `POST /reception/{id}/override`, `GET/POST /sources`, `GET/POST /vehicles`, `GET /quality-rules` (+configure), `GET /centres`, `GET /dashboard/summary`, `GET /audit-logs`, `GET /health`, `GET /health/db`.
- **PostgreSQL / EF Core**: two real migrations (`InitialCreate`, `AddMilkAnalyserFields`); `db.Database.Migrate()` runs unconditionally at startup in every environment (not gated to Development) — real migration history is used, `EnsureCreated()` is not.
- **Authentication**: see §11.
- **Endpoints**: authorization present on essentially all of them (see §11's one caveat).
- **Health checks**: `/health` = pure liveness (no dependency check), `/health/db` = real Postgres reachability check via `AddNpgSql` — a deliberate, documented separation of the two concerns.
- **Swagger**: gated behind `app.Environment.IsDevelopment()` — will not appear in a production deployment.
- **Seed data**: `DevelopmentSeeder` runs only when `ASPNETCORE_ENVIRONMENT=Development` — will not leak into production.
- **Secrets**: base `appsettings.json` has zero secrets (explicit comment directing production to environment variables `ConnectionStrings__CcmcDb` / `Jwt__Secret`); `appsettings.Development.json` has an obviously-fake, clearly-labeled dev placeholder secret.
- **CORS**: none configured — deliberate, documented (native WPF client via `HttpClient`, not a browser SPA, so CORS doesn't apply).
- **Idempotency**: see §10.
- **Contracts** (`CCMC.Contracts`): shared DTOs, `PermissionCodes`, `WireEnums`, a `FlexibleDecimalJsonConverter` — controllers consume these directly, no divergent shape found vs. what the client's `HttpCloudApiClient` sends.

## 13. Testing

- **Update (2026-09-12, latest — after Docker/Neon setup + JWT bug fix)**: **Windows client 155/155**, **Cloud 35/35** (+1 discriminating rate-rounding test, +2 JWT configuration regression tests since the figures below were recorded). **`dotnet test CCMC.sln` combined total: 190/190 passed.** Cloud suite run against a real reachable PostgreSQL each time.
- **Update (2026-09-12, after Rate Calculation implementation)**: a later session obtained access to a real local PostgreSQL instance (a fresh, isolated `pg_ctl`-managed instance, not the machine's own stopped service) and confirmed the cloud suite genuinely passes. Counts at that point:
  - **Windows client** (`tests/CCMC.Tests`): **154/154 passed, 0 failed, 0 skipped** (126 pre-existing + 28 new: `RateCalculationServiceTests.cs` and rate-specific additions to `ReceptionWorkflowServiceTests.cs`/`ReceptionRepositoryTests.cs`/`SchemaMigratorTests.cs`/a new `RateFormulaSettingsRepositoryTests.cs`).
  - **Cloud** (`tests/CCMC.Cloud.Api.Tests`): **33/33 passed, 0 failed, 0 skipped** (20 pre-existing + 13 new: `RateFormulaSettingsTests.cs` and rate-specific additions to `ReceptionTests.cs`), run via real `WebApplicationFactory<Program>` against a real, freshly migrated `ccmc_cloud_test` PostgreSQL database — genuinely verified, not the "structurally sound but unverified" state the original audit had to report.
  - **`dotnet test CCMC.sln` combined total: 187/187 passed.**
- Below is the **original audit's own text**, preserved for the historical record of what this document could and could not certify at the time:

> Windows client (`tests/CCMC.Tests`): **126/126 passed, 0 failed, 0 skipped** (actual run in this session), real temp-file SQLite per project convention (not mocked). Coverage: Auth (2 files), Devices (3, incl. both parsers + `DeviceManager`), Domain (3: Backoff, QualityValidation, ReadingProvenanceTracker), Persistence (5: Outbox, OverrideOutbox, Reception, SchemaMigrator, fixture), Reception workflow (1), Serial (1), Sync (3). No rate-calculation tests exist — there is no feature to test yet.
>
> Cloud (`tests/CCMC.Cloud.Api.Tests`): 5 test files, 20 executable tests (Auth 6, Authorization 5, Health 2, Reception incl. idempotency/overrides 12 across Facts/Theories) via real `WebApplicationFactory<Program>` against a dedicated `ccmc_cloud_test` database — not mocked, per project convention. Actual run in this session: 20 Failed, 0 Passed — every test fails at host startup with `Npgsql.NpgsqlException: Failed to connect to 127.0.0.1:5432` because no PostgreSQL server is reachable in this analysis environment. A `postgresql-x64-16` Windows service exists on this machine but is Stopped, and starting it required elevated privileges not available in this session.
>
> `dotnet test CCMC.sln` combined total: 126 (client, verified passing) + 20 (cloud, structurally sound but unverified in this session) = 146 tests exist in the solution. Do not repeat STATUS.md's "110/110 passing" figure — it is stale.
- **Hardware-in-the-loop tests**: none exist, and none are silently faked — explicitly listed as not-yet-covered in STATUS.md, consistent with the "hardware-in-the-loop is its own tier" testing philosophy in `CLAUDE.md`.

## 14. MVP Readiness

### Complete
- Reception workflow (minus receipt/printout)
- **Rate Calculation (BRD §25)** — implemented, tested (187/187 total including a live-PostgreSQL cloud run), verified end-to-end (updated 2026-09-12; see §4) — **but see §4/§21's 2026-09-18 correction: no in-app way to configure it existed until 2026-09-18, so this was not actually usable by a Manager through the running application until then**
- Quality validation + manual ACCEPT/HOLD/REJECT (auto-accept correctly dormant)
- Offline-first SQLite + idempotent cloud sync
- Cloud auth/RBAC/audit
- Weighing scale protocol (decoded, self-documented as hardware-verified, one open config discrepancy to reconcile)
- Manual fallback / reading provenance

### Partial
- Milk analyser (software parser solid; physical serial link unverified)
- Local database schema (functionally complete, table shape diverges from BRD's illustrative names — documentation nit, not a functional gap)
- Windows Behaviour §13 (works when run from a built folder; no installer yet, so not truly "no developer tooling" for an end user)

### Blocking
- **Source Hierarchy field** — zero implementation (small, but explicitly in-MVP scope per §17).
- **Notification hook/abstraction** — zero implementation, not even a dormant interface (explicitly in-MVP scope per §17, described as "hook/abstraction only" — a small, low-risk item to close).
- **Reports (§16)** and **Receipt/Result (§19 end-state)** — no reporting or receipt/printout capability exists anywhere.

### Can Defer
- Physical hardware verification of the milk analyser (blocks a "hardware-verified" claim but does not block software/UI testing of the rest of the MVP).
- ESSAE/Videocon serial-config discrepancy reconciliation (does not block current functionality — the codebase already consistently uses the Videocon/COM4/2400 fact only).
- Windows installer (WiX MSI) — needed for real end-user deployment, not needed to demo/test the MVP from a built folder.
- Client-side RBAC UI gating (server already enforces the real control).
- Override-endpoint idempotency key (a real but narrow, low-likelihood gap given typical usage patterns).

## 15. Online Deployment Research

Research conducted 2026-09-12, cross-checked against official pricing/docs pages where reachable. Free tiers move fast; re-verify against the linked sources immediately before acting on this table.

**Backend hosts (ASP.NET Core 8):**

| Platform | ASP.NET Core | Free/$0 | Sleeps | Limits | Card Required | Recommendation |
|---|---|---|---|---|---|---|
| Render (Web Service) | Yes, via Docker | Yes | Idles after 15 min, ~30-60s cold start | 750 shared instance-hrs/mo, 512MB RAM/0.1 vCPU | No | ✅ Recommended |
| Railway | Yes, Docker/Nixpacks | No sustainable free tier — $5 trial (30 days) then $1/mo credit only | N/A | Volumes deleted 30 days after trial credit expires | No for trial | Not recommended — not durable $0 |
| Fly.io | Yes, Docker | None for new orgs since 2024 | N/A | Pay-as-you-go only | Yes, mandatory | Not recommended |
| Azure App Service (F1) | Yes, native | Always-free | Idles ~20 min | 60 CPU-min/day, 1GB RAM, **no custom domain on F1** | Yes (identity only) | Viable but no custom domain |
| Azure Container Apps | Yes, Docker | Permanent monthly free grant | Scales to zero | 180,000 vCPU-sec + 360,000 GiB-sec + 2M req/mo | Yes | Good alternative |
| Google Cloud Run | Yes, Docker | Always Free | Scales to zero | 2M req/mo, 180K vCPU-sec, 360K GiB-sec/mo | Yes | Good alternative |
| AWS (App Runner/EB/EC2) | Yes | Restructured July 2025: time-boxed $100-200 credit for new accounts, not durable | Varies | Complex, billing risk | Yes | Not recommended for this use case |
| Koyeb | Yes, Docker | 1 free service, 512MB/0.1vCPU | Sleeps after 1hr idle | Free Postgres capped at 5 active hrs/mo (unusable as real DB) | Uncertain post-2025 acquisition | Not recommended |
| Northflank | Yes, Docker | "Sandbox" plan, always-on (no sleep) | None | 2 services + 1 DB | Yes | Backup option if no-sleep matters more than no-card |

**PostgreSQL hosts:**

| Platform | Storage | Sleeps/Pauses | Backups | Expiry | Card Required | Recommendation |
|---|---|---|---|---|---|---|
| Neon | 0.5GB/project | Scale-to-zero after 5 min idle (always resumes) | 1 manual snapshot, 6hr PITR | None — permanent free plan | No | ✅ Recommended |
| Supabase | 500MB | Project pauses after **1 week** inactivity (tightened Feb 2026) | None on free plan | Resumable manually | Not required | Good alternative |
| Render Postgres | 1GB | Always running while it exists | **None** | **Expires 30 days after creation**, 14-day grace period | No | Avoid — data-loss trap for a semester project |
| Railway Postgres | Tied to compute credit | Deleted with volumes 30 days after trial credit expires | None | Same as Railway compute | No for trial | Not recommended |
| ElephantSQL | — | — | — | **Confirmed permanently shut down** (Feb 28, 2025) | — | Do not use — no longer exists |
| Aiven for PostgreSQL | 1GB | Powered off if idle, not deleted | Included even on free tier | No stated hard expiry | No | Viable backup |
| Koyeb Postgres | 1GB | 5 active hrs/mo cap | Unclear | Effectively unusable as a persistent store | Uncertain | Not recommended |

## 16. Recommended MVP Hosting

**Update (2026-09-12):** the Neon half of this recommendation is now actually set up (project `fancy-cherry-25725711`, branch `production`) and verified — migrations apply cleanly, health checks pass. `CCMC.Cloud.Api` is containerized (`Dockerfile` + `compose.yaml`) and verified running locally against both a local Docker PostgreSQL and against Neon, controlled purely by environment variables (now `.env`/`.env.docker`/`.env.neon`, switched via `Copy-Item .env.docker .env -Force` / `Copy-Item .env.neon .env -Force` — see `.env.example`). See STATUS.md "Docker Compose + Neon Setup" and "Application Bug Fixes" for the full verification detail, including one honestly-reported bug batch: a real user manually tested the WPF client and found 6 bugs (5 sharing one root cause — a stale `CloudApi:BaseUrl` pointing at port 5000 instead of the actual Docker-mapped 8081), all fixed and verified via a real end-to-end harness.

**Update (2026-09-15):** the "Neon/production has zero users and no administrative bootstrap mechanism" limitation noted below (and in §17) is resolved — see §19 "Checkpoint (2026-09-15)" at the end of this document and STATUS.md "Neon Production Bootstrap (2026-09-15)" for the full writeup. **Render deployment itself still has not happened** — it is now explicitly BLOCKED (repository access, not a technical gap) rather than merely "not started"; see §19.

**Backend: Render (Web Service, Docker deploy). Database: Neon (PostgreSQL, free plan).**

```
Windows WPF (unchanged, runs on the operator's PC)
        ↓ HTTPS
Render Web Service (CCMC.Cloud.Api, Docker container)
        ↓ standard Npgsql connection string
Neon PostgreSQL (free, serverless)
```

**Why**: both are genuinely $0 with no credit card required anywhere in the chain — important for a student project. Render's git-push (or Docker-image) deploy flow is the simplest available redeploy loop: push to GitHub, Render rebuilds and redeploys automatically, no CLI/IaC step to remember. Render's 750 free instance-hours/month comfortably covers a single always-present-during-testing API; the well-known ~15-minute idle → ~30-60s cold start is a bounded, demo-tolerable cost (warm it up shortly before a live demo). Neon uses a standard Npgsql-compatible connection string that plugs directly into the existing `Npgsql.EntityFrameworkCore.PostgreSQL` setup with zero code changes, and — critically — Neon's free plan does **not** force-delete data after a fixed window, unlike Render's own free Postgres (which expires 30 days after creation) — that expiry behavior would be a serious trap for a project graded across a semester with milestones spread over months. Neon's 5-minute scale-to-zero is faster than Render's own cold start anyway, so in practice the API's cold start dominates total wake latency, not the database's.

The one thing to actively manage: Neon's 0.5GB storage / 100 CU-hr/month compute caps are hard limits — fine for MVP-scale test data, not something to grow into without a plan change.

**Second-best alternative: Google Cloud Run (Docker) + Supabase (PostgreSQL).**

Tradeoff vs. the primary pick: both require a credit card at signup (GCP for account verification), which conflicts with the "no card if possible" preference and is the main reason this is #2. In exchange: Cloud Run is a first-class container platform from a major cloud with a more durable long-term free-tier commitment than smaller platforms have shown (Railway and Fly.io both scaled back sharply in 2023-2024), and its Always Free grant (2M requests, 180K vCPU-sec/month) is generous for a low-traffic student demo. Supabase's free Postgres also avoids forced data deletion, but its 1-week-inactivity project pause (tightened Feb 2026) is more aggressive than even Render Postgres's 30-day expiry window — it would need a scheduled weekly "ping" to stay awake between milestones, an extra thing to remember that Render+Neon doesn't require. Deployment is also more CLI/config-heavy (`gcloud run deploy` or a Cloud Build trigger) than Render's dashboard-driven Docker flow.

**Explicitly ruled out**: Railway and Fly.io (no sustainable $0 tier left as of 2024-2026), ElephantSQL (shut down Feb 2025), AWS (restructured to a time-boxed credit for new accounts in July 2025, too much billing complexity/expiry risk for this use case), Koyeb (5-active-hour/month free Postgres cap makes it unusable as a real backing store, and free-tier access itself is uncertain post-2025 acquisition).

## 17. Deployment Blockers

**Update (2026-09-12):** both Critical items below are resolved — kept here, marked done, for traceability.

### Critical
- ~~No `Dockerfile` anywhere in the repository~~ — **Resolved.** `Dockerfile` + `compose.yaml` now exist, built and verified running (see STATUS.md "Rate/Amount Semantics Verification + Docker" and "Docker Compose + Neon Setup").
- ~~Cloud test suite has never been confirmed passing in any environment~~ — **Resolved.** 35/35 cloud tests verified passing against a real local PostgreSQL; the API itself additionally verified running against real Neon PostgreSQL.

### Important
- ~~No explicit Kestrel port/URL binding configured~~ — **Resolved.** `Program.cs` now explicitly honors a `PORT` environment variable (Render's convention), confirmed the container correctly binds `0.0.0.0`/all interfaces regardless.
- **No HSTS configured** (`UseHttpsRedirection` is present, so this is defense-in-depth only, not a functional gap). Still open.
- **`[RequirePermission]`-omission fallback is "authenticated user allowed," not "denied"** — currently harmless because every current endpoint is correctly annotated, but there is no structural safety net if a future endpoint is added without the attribute. Still open.
- **New (found via Docker testing, since fixed): `JwtOptions.Issuer`/`Audience` had no default**, causing every authenticated request to fail with 401 whenever only `Jwt__Secret` was supplied (a very plausible real deployment shape). Fixed by giving `JwtOptions` real defaults and unifying token issuance/validation onto a single `IOptions<JwtOptions>` instance — see STATUS.md for the full writeup. Locked down with 2 new regression tests.
- ~~New: Neon/production has no administrative bootstrap mechanism~~ — **Resolved 2026-09-15.** `ProductionBootstrapSeeder` now exists (env-var-gated, idempotent) and was used to create real Admin/Manager/Operator accounts and one Chilling Centre in Neon `production`. See §19 "Checkpoint (2026-09-15)".

### Optional
- No rate-limiting on `/auth/login` (brute-force concern, acceptable for a college-project pilot, worth hardening before any real production use).
- The ESSAE/Videocon scale-configuration discrepancy in legacy documentation should be reconciled with a human decision before any further weighing-scale protocol work, per STATUS.md's own flag.
- STATUS.md's "Hardware Verification" section and top-level test-count figure are stale relative to the actual current code and should be refreshed (see §14 below) — this is a documentation hygiene item, not a deployment blocker, but it will keep confusing future sessions (including future AI sessions) if left as-is.

**Stale documentation found during this analysis** (reported here per this task's Phase 2/10 instructions — not corrected, since this document must not modify other files):
- `STATUS.md`'s dedicated "Hardware Verification" section states the Videocon protocol is "still unknown — not captured" and that `ReadWeightAsync` throws `DeviceProtocolNotEstablishedException`. The actual current code (`VideoconWeightFrameParser.cs`, `VideoconWeighingScaleAdapter.cs`, committed in `4e31b0c` "Initial Build -- Scale working", 2026-09-05) shows the protocol fully decoded, self-documented as verified via live captures, throwing `DeviceParseException` only for an individual read that lacks a complete frame — a materially different and more advanced state than that STATUS.md section describes. That section was written before this commit and was never updated afterward.
- `STATUS.md`'s "Current Objective" header cites `dotnet test CCMC.sln` → "110/110 passing," a figure that predates the most recent commit's ~21 new tests (Ekomilk parser + reception workflow tests) and was never updated — see §13 above for the actual current counts.
- `context.md`'s closing line states "Nothing has been committed to git" — stale; `git log` shows 11 real commits on this branch, most recently "Updated UI and Milk Parser Added."
- `context.md`'s "Current Development State" summary made no mention of Rate Calculation (BRD §25) at all, in either its "confirmed working" or "genuinely not done" lists, at the time of the original audit. **This has since been updated** (2026-09-12) as part of implementing the feature — see `context.md`'s and `STATUS.md`'s own entries.

## 18. Next Steps

Ordered by what most directly closes remaining BRD-vs-implementation gaps for MVP readiness. Item 1 below (Rate Calculation) was completed on 2026-09-12, after the original version of this list was written — kept here, marked done, so the ordering/reasoning stays traceable.

1. ~~Implement Milk Rate Calculation (BRD §25)~~ — **Done (2026-09-12)**: rate-formula settings concept (Value1/Value2/TsRate per centre), the two-mode calculation function matching the formula exactly, live recompute wired into the reception screen, and cloud sync of the computed values (trusted verbatim, not recomputed cloud-side) — all implemented and verified (187/187 tests, real end-to-end run against a live PostgreSQL). See §4.
2. **Reconcile the Videocon vs. ESSAE serial-configuration discrepancy** with the project owner — a small human decision, currently blocking nothing but explicitly flagged as needing resolution before further scale work.
3. **Refresh `STATUS.md`'s "Hardware Verification" section and test-count figures** to match current code — a documentation-only fix, but one that will keep misleading anyone (human or AI) who reads STATUS.md without independently checking the code, as this analysis had to do.
4. **Add a `Dockerfile` for `CCMC.Cloud.Api`** and run the cloud test suite once against a real reachable Postgres — both are prerequisites for the recommended Render+Neon deployment and for actually trusting the cloud test suite's 20 tests as verified rather than merely well-formed.
5. **Implement the Source Hierarchy field and the Notification hook/abstraction** — both explicitly in MVP scope (§17), both small, neither has any code yet.
6. **Build a minimal Receipt/Result view** to close the BRD's Final Solution Vision (§19) end-state — Rate/Amount now exist (see item 1) and would be shown on it.
7. **Physically test the Ekomilk KAM98-2A analyser** against real hardware to upgrade its status from software-verified to hardware-verified, once a unit is available.

## 19. Checkpoint (2026-09-13)

Chronological summary of what happened since §17/§18 above were written,
so this document remains a complete record without needing to be re-derived
from git log.

**Dockerization (2026-09-12, completed before this checkpoint):** items 2-4
of §17's Deployment Blockers were resolved — `Dockerfile` (multi-stage,
`sdk:8.0` build → `aspnet:8.0` runtime) and `compose.yaml` (project `cc-mc`,
containers `cc-mc`/`cc-mc-postgres`, network `cc-mc-network`) were added and
verified running; the cloud test suite (35 tests) was confirmed passing
against a real Postgres; Neon project `fancy-cherry-25725711` (branch
`production`) was linked and confirmed reachable with migrations applying
successfully.

**Environment repair + application bug fixes (2026-09-12):** manual testing
of the actual WPF application surfaced 6 real problems, traced to one shared
root cause plus genuinely independent gaps:
- **Root cause (5 of 6 symptoms):** `CCMC.Desktop/appsettings.json`'s
  `CloudApi:BaseUrl` was still `http://localhost:5000/`, a stale pre-Docker
  default — the real API lives at `http://localhost:8081/` (the Compose
  host port). This caused every online request to fail as a connection
  refusal, which `AuthenticationService` correctly (by design) classified
  as a network failure and fell back to offline mode — explaining "login
  succeeds but shows OFFLINE MODE," the blank rate calculator (master-data
  sync, including rate-formula settings, is skipped for offline sessions),
  and "the app appears offline." **Fix:** corrected the `BaseUrl` to
  `http://localhost:8081/`. No authentication/offline-fallback logic was
  changed — that design was already correct.
- **History screen missing Rate/Amount:** pure UI gap, `ReceptionHistoryWindow.xaml`
  never had the columns bound. Fixed by adding `Rate`/`Amount`/`ReadingSource`
  `DataGridTextColumn`s — no data-layer or calculation change.
- **No Source/Vehicle management UI for Manager/Admin:** the server already
  enforced `SOURCE_CREATE`/`VEHICLE_CREATE` correctly; the client simply had
  no UI or `ICloudApiClient` methods to use it. Fixed by adding
  `CreateSourceAsync`/`CreateVehicleAsync` (mirroring the existing
  `OverrideReceptionAsync` status-classification pattern) and an Add
  Source/Vehicle panel in each window, gated client-side (UX-only) by the
  same permission codes the server already required.
- **Enter key not submitting login:** `LoginButton` was missing
  `IsDefault="True"` — WPF's standard default-button mechanism. One-line fix.

Environment file naming was also redesigned in this pass to
`.env`/`.env.docker`/`.env.neon`/`.env.example` (all three secret-bearing
files gitignored, only `.env.example` tracked with placeholders), and
`compose.yaml`'s `postgres` service was made to skip entirely (not just idle)
in Neon mode via Compose `profiles: [docker]`.

All fixes were verified via a real end-to-end scratch harness exercising the
actual production service classes against the real running Docker API
(30/30 checks passed) — see `STATUS.md` "Application Bug Fixes (2026-09-12)"
for full detail. Literal GUI mouse-click/keyboard interaction remained
untested (no GUI automation tool available in this environment) and was
reported as such, not overstated.

**Documentation checkpoint (2026-09-13, this pass):** no code changed.
`architecture.mmd` was created (full-layer Mermaid diagram, devices through
Render); `HOW_TO_RUN.md` was fully rewritten (it had remained entirely
pre-Docker/stale until now); `README.md` and `context.md`'s remaining
stale `localhost:5000` references were corrected/reframed as a secondary
non-Docker alternative; `STATUS.md` gained a top-level
COMPLETE/PARTIAL/NOT-IMPLEMENTED/NEXT checkpoint section; `CLAUDE.md` gained
an operational-state section for future sessions. `dotnet test CCMC.sln`
was re-run fresh and confirmed 190/190 passing (155 client + 35 cloud, the
latter against a disposable local Postgres container).

**Remaining gaps (unchanged, not solved in this pass — see `STATUS.md`
"Checkpoint (2026-09-13)" for the full list):** no production user-bootstrap
mechanism for Neon; Render deployment not performed; literal WPF GUI
automation not available in any environment used so far; physical Ekomilk
hardware serial link not yet verified.

**Next step:** production admin bootstrap, then Render deployment (see
`HOW_TO_RUN.md` §9) — explicitly not started in this checkpoint.
8. **Defer**: Reports module (§16), WiX installer, override-endpoint idempotency key, client-side RBAC UI gating — all real gaps, none blocking a first online MVP test deployment.

## 20. Checkpoint (2026-09-15) — Neon Production Bootstrap / Deployment Pause

Chronological summary of what happened since §19 above, so this document
remains a complete record without needing to be re-derived from git log.
Full technical detail for both items below lives in STATUS.md — this
section is the narrative summary `progress.md` is for.

**Neon production bootstrap (code + real database work):**
- Repository/database state was inspected first, not assumed: Neon
  (`fancy-cherry-25725711`, branch `production`) already had all 3 EF Core
  migrations applied (confirmed via `__EFMigrationsHistory`) but zero rows
  in every table. Local Docker Postgres was separately inspected and found
  to hold only `DevelopmentSeeder` output plus manual QA-testing rows
  (duplicate transactions from button-mash testing) — judged not real
  business data and deliberately **not** migrated to Neon.
- A new `ProductionBootstrapSeeder` + `ProductionBootstrapOptions`
  (`src/CCMC.Cloud.Infrastructure/Seed/`) was added, wired into
  `Program.cs` right after migrations. It runs in any environment but is a
  complete no-op unless `Bootstrap:AdminEmail` configuration is present;
  every identity/credential comes from `Bootstrap__*` environment
  variables (documented as placeholders in `.env.example`), nothing
  invented. It is idempotent (existing users detected by email, password
  hash never overwritten) and fails fast on invalid input (a real
  11-character admin password was caught and rejected before any DB write
  during this session's own run). `DevelopmentSeeder`'s role/permission
  grants and upsert helpers were extracted into a shared `SeedHelpers` so
  the two seeders cannot drift apart on RBAC — `DevelopmentSeeder` itself
  is otherwise unchanged. 190/190 tests reverified passing after the
  refactor.
- Bootstrapped into Neon `production`, by explicit human decision on
  identities: one Chilling Centre (`BLR-CC-01` / "Chilling Centre -
  Bangalore" — a placeholder identity, same as the dev seed, to be renamed
  once the real centre is known), one Admin (`admin@ccmc.local`, all
  centres), one Manager and one Operator (`manager1@ccmc.local` /
  `operator1@ccmc.local`, both scoped to `BLR-CC-01`), plus the same BRD
  §10 global quality rules `DevelopmentSeeder` already used. Verified
  directly against Neon afterward — correct role/permission counts (Admin
  15, Manager 15, Operator 7), correct centre scoping, real ASP.NET Core
  Identity PBKDF2 password hashes (not plaintext). `RateFormulaSettings`
  deliberately left unseeded, same as `DevelopmentSeeder` (no fabricated
  defaults).
- A real secret-exposure incident occurred during this session: a
  diagnostic shell command intended only to check line numbers printed
  `.env.neon`'s full contents — including the real Neon DB password, JWT
  signing secret, and all three (weak, dev-seed-pattern) bootstrap
  passwords — into the session transcript. Disclosed immediately; the
  human chose to rotate these credentials themselves rather than in-session.
  **Until confirmed rotated, treat those specific credentials as
  compromised**, not merely "worth rotating eventually."
- A UI/UX redesign pass (full visual pass, then a same-day refinement
  pass fixing contrast/alignment/off-screen issues) also landed this same
  day — presentational only, no domain/application/infrastructure/rate/
  quality/sync logic touched, 190/190 tests unaffected. See STATUS.md
  "UI/UX Redesign (2026-09-15)" / "UI/UX Refinement Pass (2026-09-15)" for
  the full detail (design system extensions, window sizing/ownership
  fixes, dashboard drill-down, history search/filter, screen-fit
  handling).

**Render deployment paused (documentation-only session, no code change):**
- The GitHub repository is owned/controlled by the CEO; the current
  session's operator does not have the access required to connect the
  private repository to Render. This is an access/permissions blocker,
  not a technical one — the Docker image and Neon backend are both
  already deployment-ready.
- This session (documentation-only, per its own explicit instruction) did
  not touch application/source/test/configuration code, did not deploy
  anything, and did not change the WPF client's `CloudApi:BaseUrl` — it
  updated `CLAUDE.md`, `STATUS.md`, `progress.md`, `HOW_TO_RUN.md`,
  `context.md`, and `README.md` to remove stale claims (mainly: the old
  "Neon has zero users"/"no bootstrap mechanism" statements) and to
  record the Render blocker accurately as BLOCKED rather than "not
  started."
- A documentation-focused secret scan was performed across every tracked
  file touched in this pass — no real secret values were found or added;
  only variable names and placeholders. Git state was checked before and
  after: branch stayed `windows-application`, HEAD unchanged by this
  pass's own edits (verified via `git rev-parse HEAD` before/after),
  nothing committed or pushed, `pranav-dev` untouched.

**Current next step (unchanged in substance, updated in framing):**
obtain Render-connect access from the repository owner/CEO → connect the
repo to Render → deploy the existing Docker image → verify Render→Neon
connectivity → point the WPF client's `CloudApi:BaseUrl` at the Render
HTTPS URL → real-world end-to-end verification, including a real first
login against Neon's new accounts (not yet exercised — see STATUS.md).
None of this has been started; see STATUS.md "Checkpoint" → NEXT for the
ordered list.

## 21. Checkpoint (2026-09-18) — Single-Window Shell, RBAC Navigation, Rate Configuration

Chronological summary since §20 above. Full technical detail lives in
`STATUS.md`'s "Single-Window Shell & Navigation Redesign (2026-09-18)",
"Visual Correction Pass + Rate Calculation Root-Cause Fix (2026-09-18)",
and "Documentation Consistency Pass (2026-09-18, later the same day)"
entries — this section is the narrative summary, same convention as §20.

**Single-window navigation (code + real UI restructuring):**
- `MainWindow` is now the only post-login top-level window (besides
  `LoginWindow`) — `ReceptionWindow`, `ReceptionHistoryWindow`,
  `SourcesWindow`, `VehiclesWindow`, `SyncStatusWindow`,
  `SettingsWindow`, `DeviceConfigurationWindow`, and `DeviceStatusWindow`
  (eight separate top-level windows previously) were converted to
  `UserControl`s under a new `CCMC.Desktop.Views` namespace, hosted in a
  single `ContentControl` that the left sidebar swaps between. No
  reception/device/rate/sync business logic was rewritten in this
  conversion — confirmed by inspection, every constructor/handler/service
  call carried over unchanged.
- **Settings** now hosts **Device Configuration** and **Device Status**
  as two additional toggled sections alongside the existing General
  section, on one screen reached via one "Settings" sidebar item — there
  are no longer separate "Device Status"/"Device Configuration" sidebar
  entries.
- **Sources/Vehicles are hidden from Operator navigation**, visible for
  Manager/Admin — deliberately checked by **role name**, not by the
  `SOURCE_VIEW`/`VEHICLE_VIEW` permission (Operator is actually granted
  both of those server-side, so Reception's own source/vehicle pickers
  keep working for an Operator; gating the *nav item* on that permission
  would not have hidden it for an Operator).
- **Reception History** gained a date-period filter (Today / Past Week /
  Past Month / Past Year / Total, defaulting to Today) that combines with
  the existing status filter and a widened search (now covering quantity,
  fat, SNF, CLR, water, protein, temperature, rate, amount, reading
  source, status, sync state, and captured date/time — previously only
  source/vehicle/transaction number).

**Rate Calculation made actually usable for the first time (root-cause
finding — see "Update (2026-09-18)" note in §2/§4 below for why this
matters):**
- The calculation formula, local/cloud persistence, sync, and history
  display were all already correct and already covered by passing tests
  (confirmed by re-reading the actual BRD `.docx` directly, not a prior
  summary — its §25 wording matches this repo's own doc comments
  verbatim). What never existed anywhere until this pass: any WPF screen,
  and any `ICloudApiClient` method, to actually call the cloud's already-
  working `PUT /rate-formula-settings`. Consequence: every reception's
  Rate/Amount was genuinely `0` in the running application, for every
  centre, for the entire time between the 2026-09-12 Rate Calculation
  work (§4 below) and this pass — the domain formula and API endpoint
  being correct was necessary but not sufficient for an actual Manager to
  ever use the feature.
- A new **Rate Configuration** screen (`Views/RateConfigurationView`) was
  added: centre selector (only the signed-in user's own accessible
  centres), a calculation-mode selector ("Fat vs SNF" / "TS Based"), the
  exact BRD formula shown read-only, editable Value 1/Value 2 or TS Rate
  fields depending on mode, the 0.22/0.36/0.32 constants shown explicitly
  as non-editable, a calculation preview using the same domain service
  Reception itself uses, and Save. Visible only to Manager/Admin in the
  sidebar (same role-based convention as Sources/Vehicles above).
- **Backend gap found and fixed in the same pass:**
  `RateFormulaSettingsService.UpsertAsync` had no centre-scoping check at
  all — any Manager/Admin with the configure permission could silently
  overwrite *any* centre's configuration, or the shared global default,
  regardless of their own centre assignment. A passing test
  (`Upsert_ByManager_CreatesNewGlobalSettings`) was actively asserting
  this as correct. Fixed with the same `CentreAccessGuard` pattern
  Sources/Vehicles already use, plus a new rule that only an
  all-centres-scoped caller may write the global default; the affected
  test was replaced and three new ones added.
- See `rateconfig.md` (new, root-level) for the manager-facing explanation
  of the feature, including the exact BRD-defined meaning of Value 1/
  Value 2 (the BRD calls them, together, "two configured base-rate
  components, used only in Fat-vs-SNF mode" — neither the BRD nor the code
  defines any more specific business meaning, and that document
  deliberately does not invent one).

**Visual fixes attempted, one still open per the developer's own
manual testing:**
- The Accepted/Hold/Rejected History status-chip sizing issue was fixed
  by giving those three chips (only) a shared minimum width, in addition
  to the height/centering fix from the 2026-09-15 pass.
- The textbox left-padding issue (Login, and the History/Sources/Vehicles
  search boxes) was restructured at the root cause — the icon was moved
  into the same `ControlTemplate` as the text input instead of being a
  separately-overlaid element with a compensating oversized `Padding`.
  **However, the developer's own commit message for this exact change
  (`dfa9ad9 "Rate config added -- text box bug still an issue"`) records
  that, per manual testing after committing, the issue is still visually
  present.** This is not glossed over: it is recorded as an open item
  here, in `STATUS.md`, and in `rateconfig.md` §13, not claimed fixed.

**Testing/build status:** `dotnet build CCMC.sln` — 0 warnings/0 errors.
`dotnet test CCMC.sln` — **197/197 passing** (159 client, up from 155;
38 cloud, up from 35), the cloud suite run against a real disposable
`postgres:16` Docker container (the local Postgres Windows service is
currently stopped and this sandbox cannot start Windows services without
elevation). A real Debug `.exe` launch was performed and its log shows
the same clean-startup signature as every prior session.

**Not verified in any of the sessions covered by this checkpoint:**
literal mouse-click GUI interaction — clicking through the new nav items,
visually confirming chip width/centering, visually confirming textbox
caret position, walking through the Rate Configuration screen as a real
Manager account end to end. No GUI automation tool has been available for
this native WPF app in any session to date. All claims about on-screen
behavior in this checkpoint and in `rateconfig.md` are sourced from
reading the actual XAML/code-behind, and from the automated test suites
covering the calculation/persistence/sync/RBAC layers beneath the UI —
not from an observed screenshot or click-through.

**Render deployment status: unchanged, still BLOCKED** (repository access
from the CEO, not a technical gap) — nothing in this checkpoint touches
that blocker either way; see §20 above and `STATUS.md`/`HOW_TO_RUN.md`
§10 for the current detail, which remains accurate.
