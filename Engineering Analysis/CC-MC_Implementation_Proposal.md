# Implementation Proposal — Chilling Centre Milk Collection & Device Integration System

**Status:** Proposal for Principal Engineer review/approval — no implementation has started.
**Basis:** `BRD_Engineering_Analysis.md` (prior analysis pass) and the underlying BRD v1.0.
**Scope of this document:** Architecture, technology choices, repo layout, database design, API boundaries, frontend/gateway structure, sync strategy, RBAC implementation, testing strategy, a realistic "tonight" slice, and exact build ordering. No code, no schemas-as-DDL, no configs — structural design only, per instruction.

Every technology choice below is a **recommendation**, not a mandate — the BRD names no required stack. Each is justified against the constraints surfaced in the prior analysis: unknown device hardware (Q2), unresolved sync idempotency (Q6/Q7), and the need to move fast tonight without boxing in the architecture decisions still pending from the business (Q1, Q3, Q4).

---

## 1. Architecture

Three deployable boundaries, matching the BRD's own split but made concrete:

```
                         ┌───────────────────────────────────────────┐
                         │                  CLOUD                     │
                         │                                             │
                         │   React SPA  ──HTTPS/WSS──▶  API Service    │
                         │  (browser)                    (stateless,   │
                         │                                horizontally │
                         │                                scalable)    │
                         │                                    │        │
                         │                                    ▼        │
                         │                          PostgreSQL (single │
                         │                          source of truth:   │
                         │                          transactions,      │
                         │                          master data, RBAC, │
                         │                          audit log)         │
                         └───────────────────▲─────────────────────────┘
                                             │ HTTPS (outbound only, from centre)
                                             │ Device Integration API + WS status channel
                         ┌───────────────────┴─────────────────────────┐
                         │         LOCAL DEVICE GATEWAY (per centre)     │
                         │  Device Manager → RS232 Mgr / Bluetooth Mgr   │
                         │  → Adapter Layer → Parser → Local Queue       │
                         │  (SQLite, durable) → Sync Manager → Health    │
                         │  Monitor                                      │
                         └───────────────┬───────────────┬──────────────┘
                                         │ RS232         │ Bluetooth
                                         ▼               ▼
                                  Weighing Scale     Milk Analyser
```

**Why this shape, not something else:** The two hard constraints in the BRD that are *not* negotiable regardless of Q1's answer are (a) physical devices must never be reachable from the public internet, and (b) reception must keep working through a temporary outage. Both of those force *some* local process at the centre that talks to the devices and can operate detached from the cloud — so the gateway boundary survives even if the specific component breakdown inside it changes later. Everything else (frontend framework, DB engine, gateway language) is a free choice, made below for velocity and for skills/tooling overlap across the three deployables.

**Cloud side is a single deployable API service, not microservices, for MVP.** RBAC, transactions, quality validation, reporting, and audit all share one transactional database and mostly one consistency boundary (a transaction's accept/reject decision, its audit record, and its dashboard visibility must be atomic). Splitting this into services now would add distributed-transaction complexity with no present benefit — revisit only if a specific module (e.g., reporting) needs independent scaling later.

**Gateway is a separate deployable per centre**, installed on a machine physically at the centre with serial/Bluetooth access. It is intentionally "dumb but durable": it does not make business decisions (accept/reject/hold), it only captures, normalizes, buffers, and forwards. Quality validation stays server-side so a compromised or outdated gateway can't fabricate an ACCEPT.

---

## 2. Technology Choices

| Layer | Choice | Rationale |
|---|---|---|
| Language (cloud API + gateway) | **TypeScript** everywhere (Node.js runtime) | One language across API, gateway, and frontend means one team can move across all three tonight, and a **shared types package** (Section 3) can define `MilkReceptionTransaction`, permission codes, and the Device Integration API contract exactly once, used by all three deployables — this alone removes a whole category of integration bugs between gateway and API. |
| Cloud API framework | **NestJS** | Gives RBAC-style guards/decorators, module boundaries, and dependency injection out of the box — matches the BRD's own module/action permission shape (Section 7 of the BRD) almost directly onto NestJS Guards + a custom `@RequirePermission()` decorator. More structure than Express, which pays off fast once RBAC, audit interceptors, and validation pipes are all cross-cutting concerns. |
| Database | **PostgreSQL** | Relational integrity for transactional data (a reception transaction referencing source/vehicle/device/user must be consistent); native row-level security is a natural fit for centre-scoped RBAC later; JSONB columns give a documented escape hatch for variable analyser parameters (density, protein, lactose, "other available parameters" per BRD Section 23) without a schema migration per new device field. |
| Frontend framework | **React + TypeScript + Vite** | Fast dev server for iterating tonight; large ecosystem for forms/tables/RBAC-aware routing; consumes the same shared types package as the API. |
| Frontend component/state layer | **A component kit (e.g. shadcn/ui or MUI) + TanStack Query for server state** | Avoids hand-building tables/forms/dialogs under time pressure; TanStack Query gives caching/invalidation for dashboard and report views without a heavy global-state framework. |
| Gateway runtime | **Node.js (TypeScript), packaged as a long-running service** | Same language as the API — the parser/normalizer logic and the Device Integration API client can share types directly. Node has mature serial (`serialport`) and Bluetooth (platform-dependent BLE/classic libraries) ecosystems, which matters once real device specs (Q2) arrive. |
| Gateway local storage | **SQLite (file-based)** | Durable local queue that survives gateway process restarts — critical for the offline-buffering requirement (BRD Section 44). No local server process to manage, which matters for unattended field machines. |
| Realtime channel (gateway↔cloud status, live dashboard updates) | **WebSocket** | Device status (Section 30 of the BRD) and sync progress are inherently push-driven; REST polling would be simpler tonight but WS is cheap to add once the transaction API exists and materially improves the "device connected / internet online" live indicators. Acceptable to defer to polling for the tonight-slice (Section 12). |
| Auth | **JWT access + refresh tokens, argon2 password hashing** | Stateless-enough for a horizontally scalable API; refresh tokens allow session revocation on deactivation (BRD Section 10). |
| Gateway↔Cloud auth | **Per-gateway API credential (issued at gateway registration), separate from user auth** | The gateway is a system principal, not a user — it should not impersonate an operator. This also lets the cloud revoke a single compromised centre's gateway without touching user accounts. |
| Deployment packaging | **Containerized cloud API (Docker); gateway packaged as a native OS service installer** | Cloud provider is intentionally left unspecified (BRD gives none) — containerization keeps that decision reversible. Gateway must run unattended on a centre machine, so it needs OS-level service registration (auto-start on boot, auto-restart on crash), independent of the cloud hosting decision. |

**Explicitly not chosen for MVP, with reasons:** Redis/caching layer (defer until RBAC lookups or dashboard aggregation actually show latency — premature now); microservices (see Section 1); a message broker like Kafka/RabbitMQ for gateway sync (the sync problem is centre→cloud, low fan-in, and doesn't need a broker — direct authenticated HTTPS calls with local durable queuing is sufficient and much simpler to operate); GraphQL (REST is a better fit for a small, well-bounded set of resources and keeps the Device Integration API contract simple to version).

---

## 3. Repo Structure

A single monorepo, since the three deployables share types and evolve together pre-launch:

```
cc-mc/
├── apps/
│   ├── web/                 # React frontend (operator/admin UI)
│   ├── api/                 # NestJS cloud backend
│   └── gateway/              # Local Device Gateway service
├── packages/
│   ├── shared-types/         # DTOs, enums, permission codes, Device Integration API contract
│   ├── device-adapters/      # Adapter interfaces + simulator/mock adapters (shared by gateway + tests)
│   └── ui/                   # (optional, once web app has enough shared components to warrant it)
├── infra/
│   ├── docker-compose/       # Local dev stack: api + postgres (+ mailhog/etc. as needed)
│   └── migrations/           # Database migration scripts, versioned
├── docs/
│   ├── brd/                  # Original BRD + prior engineering analysis
│   └── adr/                  # Architecture Decision Records — one per major choice in this doc
└── package.json               # Workspace root (pnpm workspaces or Turborepo)
```

**Why monorepo over polyrepo:** the gateway and API must agree on the Device Integration API contract byte-for-byte; a shared-types package with a single build step enforces that agreement at compile time rather than via documentation that drifts. Split into separate repos later only if gateway and API ship on genuinely different release cadences (likely, once stable) — not a concern for MVP.

**`docs/adr/`** is deliberately included from day one: several of the P0/P1 questions from the prior analysis (Q1 architecture mandate, Q3 tenancy, Q6/Q7 sync ID scheme) will get answered as one-line decisions during build — each should be captured as a short ADR so the reasoning isn't lost.

---

## 4. Database Design

Logical schema (entities, key relationships, and design notes) — not DDL. All tables get standard `id`, `created_at`, `updated_at`; omitted below for brevity except where lifecycle matters.

**Tenancy/scoping spine**
- `chilling_centre` — id, name, location, status. Root scoping entity for almost everything below.
- `user` — id, email, password_hash, status (active/deactivated), timestamps.
- `user_centre_assignment` — user_id, centre_id (nullable centre_id, or a separate `all_centres` flag, to represent "assigned to all centres" per BRD Section 9 — this needs an explicit modeling decision, see note below).
- `role` — id, name, is_system_default (bool, to distinguish seeded roles from org-created ones).
- `permission` — id, code (e.g. `MILK_RECEPTION_CREATE`), module, action. Seeded from the permission code list in BRD Section 50.
- `role_permission` — role_id, permission_id.
- `user_role` — user_id, role_id (supports a user holding more than one role, which the BRD doesn't rule out).

*Design note on "assigned to all centres" (BRD Section 9):* modeling this as a row per centre doesn't scale cleanly and modeling it as a null centre_id is ambiguous with "no centres." Recommend a dedicated `scope_type` enum on the assignment (`SINGLE`, `MULTIPLE` via multiple rows, `ALL`) so the RBAC check has one unambiguous rule to evaluate, rather than inferring intent from null-handling.

**Master data**
- `source` — id, source_code, name, location, contact, milk_type, status.
- `vehicle` — id, vehicle_number, tanker_number, driver_name, driver_mobile, capacity, status.
- `device` — id, name, device_code, manufacturer, model, device_type (SCALE/ANALYSER), centre_id, status.
- `device_configuration` — device_id, connection_type (RS232/BLUETOOTH), connection_params (JSONB — COM port/baud/etc. for RS232, MAC/pairing for BT; JSONB chosen here specifically because the field set differs by connection type and this table should not need a migration per new parameter), parser_config (JSONB — message format/field mappings/unit conversion, same reasoning).
- `quality_rule` — id, parameter (FAT/SNF/CLR/Temperature/etc.), min_value, max_value, and **explicitly nullable** milk_type_id and centre_id foreign keys so a rule can be global or scoped, pending resolution of Q5 from the prior analysis. Building this nullable-scoped from day one avoids a schema change later regardless of which way Q5 is answered.
- `rejection_reason` — id, label, is_active — a real master table rather than free text, resolving Q12 in the direction that's safer for reporting (structured reasons enable the Exceptions report to aggregate meaningfully); a free-text `note` field alongside it preserves flexibility.

**Transactional core**
- `milk_reception_transaction` — id, transaction_number (see Section 8, Sync Strategy, for numbering scheme), centre_id, source_id, vehicle_id, milk_type, operator_user_id, quantity fields (gross/tare/net/unit), status (ACCEPTED/REJECTED/HOLD), reading_source (DEVICE/MANUAL), rejection_reason_id (nullable), reason_note (nullable), received_at (device/operator-reported time), created_at (server time) — **both timestamps kept deliberately separate**, since an offline-captured transaction's "true" event time and its "arrived at cloud" time can differ by hours; reporting should default to `received_at`.
- `milk_quality_reading` — id, transaction_id, FAT, SNF, CLR, density, temperature, added_water, protein, lactose, other_parameters (JSONB for anything not enumerated — matches BRD's "other available parameters"), source (DEVICE/MANUAL), device_id (nullable, null when manual).
- `transaction_override` — id, transaction_id, original_status, new_status, user_id, reason, approved_by_user_id (nullable), created_at. Kept as its own table rather than columns on the transaction so a transaction can theoretically be overridden more than once with full history (the BRD's audit requirement implies history matters more than "latest state").
- `device_reading_log` — id, device_id, transaction_id (nullable — a reading can arrive before a transaction is bound to it), raw_message, parsed_result (JSONB), status, error (nullable), created_at. This is the technical communication log from BRD Section 32, distinct from the business-level `milk_quality_reading`.

**Sync/offline**
- `sync_batch` — id, gateway_id, status (PENDING/COMPLETED/FAILED), transaction_count, created_at, completed_at. One row per sync attempt from a gateway, for observability into Q6's eventual mechanism.
- `gateway` — id, centre_id, api_credential_id, last_seen_at, status. The gateway-as-a-principal entity flagged as missing from the BRD's own data model in the prior analysis.

**Audit**
- `audit_log` — id, user_id, role_at_time, centre_id, action, resource_type, resource_id, old_value (JSONB), new_value (JSONB), reason (nullable), created_at. JSONB for old/new value specifically because the audited resources are heterogeneous (BRD Section 48 lists a dozen different action types across different entities) — a generic diff column avoids one audit table per entity type.

**Indexing notes (not exhaustive, flagged for the Principal Engineer's DBA pass):** `milk_reception_transaction` needs composite indexes on `(centre_id, received_at)` for dashboard/report queries and on `(centre_id, status)` for the exceptions report; `audit_log` needs `(resource_type, resource_id)` and `(user_id, created_at)`; RBAC lookup tables are small and read-heavy, good candidates for in-process caching before reaching for Redis.

---

## 5. API Boundaries

Two distinct API surfaces — deliberately not one shared API, because the frontend and the gateway have different trust levels, payload shapes, and versioning needs.

### 5.1 Frontend ↔ Cloud API (REST, user-authenticated, JWT)

| Resource group | Representative endpoints | Notes |
|---|---|---|
| Auth | login, refresh, logout, password reset | Session lifecycle per BRD Section 10 |
| Users/Roles/Permissions | CRUD on each, plus role-permission and user-role assignment endpoints | Guarded by `USER_*`/`ROLE_*` permission codes |
| Sources / Vehicles | CRUD | Guarded by module permission + centre scope |
| Devices / Device Configuration | CRUD, plus a `POST /devices/:id/test` endpoint that proxies a live test request to the gateway via the WS/command channel | Device Support/Admin only |
| Quality Rules | CRUD | `QUALITY_CONFIGURE` permission |
| Reception Transactions | create (manual path), list/get, `POST /:id/override` (accept/reject override), `POST /:id/receipt` | The device-driven creation path comes through the Device Integration API, not this one — see below |
| Dashboard | aggregate summary endpoint(s), scoped server-side to caller's centre access | Read-only |
| Reports | one endpoint per report type with filter params, plus `?format=csv/xlsx/pdf` export variants | `REPORT_VIEW`/`REPORT_EXPORT` |
| Audit Logs | list/query with filters | `AUDIT_VIEW` |

Every endpoint in this surface goes through the same permission-guard pipeline described in Section 9 (RBAC Implementation) — there is no endpoint that is "just" behind login without an explicit permission check, per BRD Section 11's explicit rule that frontend-only restriction is insufficient.

### 5.2 Gateway ↔ Cloud API (Device Integration API, gateway-credential-authenticated)

| Endpoint (conceptual) | Direction | Purpose |
|---|---|---|
| Gateway registration/handshake | Gateway → Cloud | Establish/renew gateway identity and credential, report gateway software version |
| Device status push | Gateway → Cloud | Live connectivity state per device, feeds the dashboard status widget (Section 30 of BRD) |
| Reading ingestion | Gateway → Cloud | Normalized quantity/quality readings tagged with the local transaction reference |
| Transaction submission (online path) | Gateway → Cloud | Real-time submission when connectivity is up |
| Transaction sync (offline-recovery path) | Gateway → Cloud | Batched submission of buffered transactions, idempotency-keyed (Section 8) |
| Configuration pull | Cloud → Gateway (gateway polls or receives via WS push) | Device configuration/parser config changes made in the web UI need to reach the gateway without a manual redeploy |
| Communication log upload | Gateway → Cloud | Populates `device_reading_log` for the Device report |

This surface is intentionally **narrow and append-mostly** — the gateway should never need to read back business data (transaction history, RBAC, reports); it only writes readings/status and pulls its own device configuration. That asymmetry is what keeps a compromised or buggy gateway from becoming a data-exfiltration path.

---

## 6. Frontend Structure

```
apps/web/src/
├── app/                # routing, top-level layout, auth/permission guarding at route level
├── modules/
│   ├── dashboard/
│   ├── reception/       # the core reception screen: source/vehicle select, live reading panel, accept/reject/hold
│   ├── sources/
│   ├── vehicles/
│   ├── devices/         # device list, config forms, test-connection screen
│   ├── quality-rules/
│   ├── reports/
│   ├── users-roles/      # user mgmt, role mgmt, permission assignment
│   └── audit-log/
├── shared/
│   ├── api-client/       # generated/typed client against shared-types + api OpenAPI contract
│   ├── auth/             # session state, token refresh
│   ├── permissions/       # `usePermission('MILK_RECEPTION_CREATE')`-style hook, drives conditional rendering only — never the sole gate
│   └── components/        # tables, forms, KPI tiles, status indicators
```

**Permission-gated rendering pattern:** a single `usePermission(code)` hook, backed by the permission set returned at login (and re-fetched on role/centre-assignment change), used to hide/disable actions the user can't perform. This is explicitly UX sugar, not security — every mutating action still round-trips to a backend guard that independently checks the same permission code, matching BRD Section 11 directly.

**Reception screen is the one screen worth over-investing in tonight and beyond** — it's the highest-frequency, highest-stakes screen (BRD's whole "Final Solution Vision," Section 60, is written from this screen's point of view). It should support both the device-driven path (live values arriving via WS) and the manual-fallback path (Section 2.7 of the prior analysis) from day one, since manual entry is the only path available before real device integration exists.

---

## 7. Gateway Structure

```
apps/gateway/src/
├── device-manager/        # owns device lifecycle: discovery, connect, disconnect, reconnect loop
├── transport/
│   ├── rs232/              # serial port handling, configurable per BRD Section 19/24 params
│   └── bluetooth/           # discovery/pairing/connection per BRD Section 20/25
├── adapters/                # one adapter per device model, implementing a common interface:
│                             #   parse(rawMessage) -> NormalizedReading
│                             # sourced from packages/device-adapters; simulator adapters live here too
├── local-store/              # SQLite-backed queue: pending transactions, pending readings, sync state
├── sync-manager/              # batches queue contents, calls Device Integration API, handles retry/backoff,
│                              # marks rows SYNCED on ack
├── health-monitor/            # device connectivity state, internet connectivity state, gateway self-health
│                              # heartbeat to cloud
└── config-client/              # pulls device/parser configuration changes made in the web UI
```

**The adapter interface is the single most important abstraction in the gateway**, since it's the direct implementation of BRD Section 27's "Device Abstraction Layer" and the thing every future device vendor plugs into. It should be designed and reviewed carefully even before a real device exists, because a simulator adapter conforming to this interface is what unblocks the rest of the gateway's development while Q2 (real device specs) is outstanding — see Section 12.

**Gateway is a single OS-level service per centre**, not one process per device — device count per centre isn't large enough to warrant per-device process isolation, and a single process makes the local SQLite queue and health monitor simpler to reason about.

---

## 8. Sync Strategy

This directly resolves Q6/Q7 from the prior analysis with a concrete recommended design (flagged as **recommended, pending Principal Engineer / business sign-off** given it touches the still-open Q1/Q3 questions):

1. **Local identity, not cloud identity, is assigned first.** When the gateway creates a transaction (online or offline), it generates a client-side idempotency key: `{gateway_id}-{local_sequence}` where `local_sequence` is a monotonic counter persisted in the gateway's own SQLite store. This key is generated *before* any network call, so it exists identically whether the transaction is submitted immediately or buffered for hours.
2. **The human-readable transaction number is assigned by the cloud, on first successful receipt**, not by the gateway. This avoids two gateways ever racing to claim the same sequential number (BRD's illustrative `MCC-2026-000123` format is preserved, just generated centrally). The gateway/operator UI can show a provisional local reference (e.g., "Pending sync — local #42") until the cloud-assigned number comes back.
3. **Cloud ingestion is an upsert keyed on the idempotency key**, not a plain insert — if the same key arrives twice (retry after a dropped ack, gateway restart mid-sync), the second submission is a no-op that still returns the original cloud-assigned transaction number. This is what actually satisfies BRD Section 44's "prevent duplicate transactions" requirement.
4. **Sync manager behavior:** attempt immediate submission on transaction creation; on failure (no connectivity, timeout, 5xx), fall back to the local queue; a background loop retries queued items with exponential backoff and a capped batch size per request; each successful ack marks the local row `SYNCED` and records the cloud-assigned number back into the local store (so the gateway's own UI/logs can show the real number once known).
5. **Reconciliation safety net:** a periodic job (both gateway-side and cloud-side) compares "transactions created locally" vs. "transactions acknowledged by cloud" counts and surfaces a discrepancy as an operational alert — this is the practical answer to "what if sync silently breaks," which the BRD doesn't address but which is a real operational risk once this runs unattended in the field.
6. **Buffer capacity is bounded, not unlimited** — the BRD only says "temporary" outages; recommend a configurable local retention (e.g., N days or N MB) with an explicit "buffer full" alarm state surfaced on the gateway's status output, rather than silently dropping data or growing SQLite unbounded. Exact threshold is a business/ops decision (relates to Q14, data retention), defaulted conservatively for the tonight-slice and beyond.

**What this does not solve, and shouldn't try to tonight:** ordering guarantees across transactions from different gateways (not required — each transaction is independent), or conflict resolution for edited-while-offline transactions (BRD doesn't describe editing an already-synced transaction from the gateway side, so this is out of scope by omission, not by design decision — worth confirming with the business it's genuinely not needed).

---

## 9. RBAC Implementation

Two enforcement points, exactly as BRD Section 11 mandates, plus a concrete mechanism for each:

**Backend (authoritative):** a `@RequirePermission('MILK_RECEPTION_CREATE')`-style guard on every mutating and every sensitive-read endpoint. The guard evaluates, per request: (a) does the user hold this permission via any assigned role, and (b) does the user's centre assignment cover the resource's `centre_id` (or is it `ALL`). Both checks happen in the same guard so there's no path where a permission check passes without the centre check also running — this directly prevents the class of bug where a correct role check accidentally leaks cross-centre data. Permission sets are computed once at login and cached in the JWT/session (not re-queried on every request for latency reasons), with an explicit **invalidation signal** (a `permissions_version` counter, bumped on role/permission/assignment change, checked cheaply on each request) so a revoked permission takes effect on the next request rather than only at next login — this matters given BRD's own "user deactivated while logged in" gap flagged in the prior analysis.

**Frontend (UX only, non-authoritative):** the `usePermission()` hook described in Section 6, driven by the same permission codes, purely to hide/disable UI — never trusted as the actual gate.

**Data model:** exactly the User→Role→Permission chain plus centre assignment described in Section 4 — no separate "policy engine" for MVP. The BRD Section 8 footnote's "configurable per organization's approval policy" (e.g., can an Operator Accept Milk) is implemented as **just another permission** (`MILK_RECEPTION_ACCEPT`) that a given role either has or doesn't — "configurable policy" and "role-based permission" are the same mechanism here, so this doesn't need a separate engine, only a well-chosen set of fine-grained permission codes from the start. This is the recommended way to avoid building the "fully dynamic policy engine" flagged as a MEDIUM–HIGH complexity risk in the prior analysis — get the fine-grained permission codes right, and the "configurability" falls out for free.

**Seeding:** the seven roles and the illustrative matrix from BRD Sections 5 and 8 are seeded as **default, editable data**, not hardcoded logic — satisfying BRD Section 5's "configurable... wherever practical" without pre-judging Q4 (whether that matrix is final).

**Audit hook:** every permission-guarded mutation that succeeds automatically triggers an `audit_log` write via a shared interceptor, rather than each module remembering to log — this is the concrete mechanism that keeps the audit trail complete without relying on every future developer remembering BRD Section 48's list.

---

## 10. Testing Strategy

| Layer | Approach | Why |
|---|---|---|
| Business logic unit tests | Quality validation engine, RBAC permission evaluation, sync idempotency logic, receipt content assembly | These are pure-ish functions with real business consequences (a validation bug wrongly accepting or rejecting milk is a business-critical defect) — highest-value place for thorough unit coverage. |
| API integration tests | Spin up the API against a real (containerized) Postgres per test run; exercise full request→guard→DB→response paths | Catches the class of bug where the guard logic is correct in isolation but wired to the wrong endpoint, or where a migration breaks a query — unit tests on the guard alone wouldn't catch this. |
| RBAC-specific test matrix | Parametrized tests: for each (role, permission, centre-assignment) combination relevant to a given endpoint, assert allow/deny | RBAC is exactly the kind of logic where "looks right in the one case I tried" hides bugs in the other 20 combinations — worth a deliberate matrix rather than ad hoc tests. |
| Device adapter tests | Run every adapter (including simulators) against a fixed set of recorded/sample raw messages, asserting normalized output | This is how adapter correctness gets verified *before* real hardware is available, and regression-tested after — directly addresses the "testing device integration" HIGH risk from the prior analysis. |
| Gateway sync tests | Simulate offline periods, gateway restarts mid-queue, and duplicate submissions against a test API instance; assert exactly-once cloud state | This is the highest-complexity, highest-risk subsystem identified in the prior analysis — it deserves scenario-based tests, not just happy-path coverage. |
| Contract tests (gateway ↔ API) | Shared-types package plus a contract test suite that fails the build if gateway and API drift on the Device Integration API shape | Prevents the two deployables from silently diverging, especially once they're released independently. |
| End-to-end tests | A small number of critical-path browser tests (Playwright): login → manual reception → accept → receipt; login → HOLD → manager override; RBAC-denied action correctly blocked in UI | Keep this deliberately small — E2E is expensive to maintain; reserve it for the flows that would be genuinely embarrassing to break. |
| Device simulator harness | A standalone tool (built early, per Section 12) that emits fake RS232/Bluetooth-shaped messages on demand | This isn't "a test" per se but is the single piece of infrastructure that unblocks nearly everything else in this table before real hardware specs (Q2) land — should be treated as a first-class deliverable, not test-only tooling. |

**Not proposed for MVP:** load/performance testing (no volume targets exist yet — see the prior analysis's flagged NFR gap; revisit once the business gives real numbers), and full device-hardware-in-the-loop CI (physical devices in a CI pipeline is a later-stage investment, not a night-one or even month-one concern).

---

## 11. Exact Dependency / Build Order Between Components

This is a dependency graph, not a calendar — items on the same tier can run in parallel if there are multiple engineers; items on later tiers are blocked on earlier ones.

**Tier 0 — Foundation (blocks everything):**
`packages/shared-types` (core DTOs: User, Role, Permission, Transaction, DeviceReading shapes) → database schema for tenancy/RBAC/master-data tables (Section 4) → auth (login/JWT) → RBAC guard mechanism (Section 9).

**Tier 1 — depends on Tier 0, unlocks the core vertical slice:**
Source/Vehicle CRUD (API + UI) → Quality Rule CRUD (API + UI, can seed a hardcoded default set initially) → Reception Transaction API (manual-entry path only) → Reception screen UI (manual-entry path) → Audit logging interceptor wired to Tier 1 mutations.

**Tier 2 — depends on Tier 1, can run in parallel with each other:**
- **Branch A (device path):** Device/Device Configuration CRUD (API+UI) → `packages/device-adapters` interface + simulator adapter → Gateway skeleton (device-manager, transport stubs, adapters) using the simulator → Device Integration API (Section 5.2) → wire gateway-submitted readings into the Reception flow alongside the manual path.
- **Branch B (reporting/dashboard):** Dashboard aggregate endpoint + KPI UI → Daily Reception + Exceptions reports (API+UI+export).
- **Branch C (RBAC completeness):** Role/Permission/User management screens, full seeding of BRD's seven default roles and permission matrix.

**Tier 3 — depends on Tier 2 Branch A:**
Local queue (SQLite) + sync manager (Section 8) → offline-mode reception (transaction creation without cloud connectivity) → reconciliation job.

**Tier 4 — depends on Tier 2 Branch A reaching a stable Device Integration API, and on real device specs (Q2) arriving from the business (external dependency, not engineering-sequenced):**
Real device adapters (replacing/joining the simulator) → device test-connection screen wired to real hardware → device communication log/report (Section 2.9 of the prior analysis, FR-RPT-06).

**Tier 5 — polish/remaining scope, low mutual dependency, schedule opportunistically:**
Remaining report types (Source-wise, Vehicle-wise, Quality), notification subsystem for device-disconnect/approval alerts, receipt formatting/print, transaction override UI polish, WS-based live status (upgrading from Tier 2's polling if that's what ships first).

**Critical path for a working, demoable system:** Tier 0 → Tier 1 → Tier 2 Branch A (through the simulator, not real hardware) → Tier 3. This path proves the entire domain model, RBAC, and the offline/sync story — the hardest engineering problems in the whole project — without waiting on Q2 at all. Real device integration (Tier 4) is deliberately decoupled from this critical path since it's externally blocked.

---

## 12. What Can Realistically Ship Tonight

Being direct about "tonight" meaning a single working session, not a sprint: the honest, defensible slice is **Tier 0 + a thin cut of Tier 1**, entirely on the manual-entry path, with everything device-related mocked or absent. Specifically:

**Realistic tonight scope:**
- Repo scaffolded per Section 3, shared-types package with the core DTOs.
- Database schema for: `chilling_centre`, `user`, `role`, `permission`, `role_permission`, `user_role`, `user_centre_assignment`, `source`, `vehicle`, `quality_rule`, `milk_reception_transaction`, `milk_quality_reading`, `audit_log` — enough tables to support one real end-to-end flow, not the full Section 4 schema.
- Login + JWT auth, with 2–3 seeded users covering distinct roles (e.g., Operator, Manager, Admin) so RBAC is demonstrable, not just present.
- The RBAC guard mechanism (Section 9), enforced on at least one real mutating endpoint (transaction creation) and one admin-only endpoint (quality rule edit) — enough to prove the pattern, not full permission-code coverage across every module yet.
- Source and Vehicle CRUD (minimal UI, list + create form).
- A hardcoded (or DB-seeded, not yet admin-editable) quality rule set — FAT/SNF/Temperature limits.
- Reception screen: select source/vehicle, **manually enter** quantity and quality values (no device, no gateway tonight), see automatic ACCEPT/REJECT/HOLD based on the quality rules, save the transaction.
- A HOLD → Manager override action, since it's one line of business logic (status change + audit record) and proves the approval workflow end to end.
- Audit log writes for the above actions (viewing them in a UI can wait; writing them can't, since retrofitting audit onto un-audited early tables is exactly the kind of cleanup worth avoiding).
- A bare-bones dashboard: today's transaction count and accept/reject/hold breakdown, scoped to the logged-in user's centre.

**Explicitly not tonight, and why:** any real device/gateway work (blocked on Q2, and even the simulator-based gateway skeleton is a Tier 2 item, realistically a next-session goal, not a tonight one, once Tier 0/1 groundwork is accounted for); offline sync (depends on the gateway existing first); reports beyond the raw dashboard count (need more transaction volume/variety to be worth building against tonight); user/role management UI (seed roles directly in the DB tonight, build the admin screens once the core flow is proven); receipt generation as a formatted artifact (a plain confirmation is enough to prove the flow tonight; formatted receipt content per BRD Section 41 is a short follow-up).

**What this slice proves, which is the actual point of shipping it tonight:** the two riskiest *non-device* design decisions in this whole proposal — the RBAC guard-plus-centre-scope pattern (Section 9) and the transaction/quality-validation/audit data model (Section 4) — get exercised end-to-end on day one, while they're still cheap to change. Everything device-related is deliberately excluded from tonight not because it's unimportant, but because it's externally blocked on information (Q2) that engineering doesn't control, and building against invented specs risks throwing the work away.

---

## Summary for Principal Engineer Sign-off

This proposal keeps every architectural boundary the BRD implies as non-negotiable (cloud/gateway split, devices never internet-exposed, backend-authoritative RBAC) while making concrete, reversible choices everywhere the BRD is silent (language, framework, DB engine, sync mechanism). The three biggest open items from the prior analysis (Q2 device specs, Q1 architecture mandate, Q3 tenancy) are each handled by building in a way that doesn't foreclose either answer: the adapter interface absorbs unknown device specifics, the component boundaries match what the BRD calls "recommended" closely enough to switch internals later without a rewrite, and the schema's `chilling_centre`-rooted scoping can extend to true multi-tenancy later by adding an `organization` layer above it without touching the RBAC mechanism itself.

Recommended immediate next step: confirm the technology choices in Section 2 (the only part of this proposal that's a genuine choice rather than a derived consequence of the BRD), then start Tier 0 tonight per Section 12.
