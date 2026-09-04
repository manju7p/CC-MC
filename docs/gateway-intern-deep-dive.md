
# CC-MC Gateway — Architecture Deep-Dive

*Reverse-engineered from `apps/gateway` as it exists on disk today (Checkpoint 5, per the package's own README). Every claim below is tagged **CONFIRMED BY CODE**, **INFERRED**, or **UNKNOWN FROM CURRENT CODE**.*

---

## 0. Repository Overview

```text
apps/gateway/
├── src/
│   ├── main.ts                    Process entrypoint
│   ├── gateway.ts                 Gateway orchestrator class
│   ├── config/                    Loads & validates config/gateway.config.json
│   ├── device/                    Device abstraction + DeviceManager + simulators
│   ├── transport/                 Byte-transport interface (UNIMPLEMENTED - no concrete class)
│   ├── parser/                    Byte-parser interface (UNIMPLEMENTED - no concrete class)
│   ├── normalization/              Reading types (ScaleReading, AnalyserReading)
│   ├── capture/                   ReceptionCaptureWorkflow + validation
│   ├── storage/                   SQLite local storage, schema, outbox, idempotency, backoff
│   ├── sync/                      SyncEngine, HttpCloudClient, auth, response classification
│   ├── health/                    In-memory health/status snapshot
│   └── logging/                   Minimal JSON-line logger
├── test/                          Jest specs, mirrors src/ layout, plus:
│   ├── cloud-integration/         Live-API integration tests (separate Jest config, needs Postgres + real API)
│   └── restart-recovery/          Kills the process mid-write to prove crash recovery
├── config/gateway.config.example.json   Template for the real (gitignored) config
├── service/                       WinSW v3 Windows-service config + install/uninstall scripts
├── packaging/build-deployment.mjs        Assembles the Windows deployment folder layout
├── package.json / tsconfig.json / jest.config.js
└── README.md
```

**Language & runtime — CONFIRMED BY CODE:** TypeScript (strict mode), compiled to CommonJS/ES2021, run on Node.js. `package.json`'s `dependencies` is `{}` — the gateway ships **zero runtime npm packages**. Everything it needs (SQLite, HTTP client, JWT decoding, UUIDs) comes from Node's own built-ins: `node:sqlite`, global `fetch`, `crypto.randomUUID()`, manual base64 decoding. `devDependencies` are build/test-only (`typescript`, `ts-node`, `jest`, `ts-jest`).

**Entrypoint — CONFIRMED BY CODE:** `src/main.ts`, compiled to `dist/main.js`.

**There is no HTTP server, no router, no controllers anywhere in this codebase.** This is the single biggest way this gateway differs from a generic "Node.js gateway" — it does not receive inbound HTTP requests at all. It is an **outbound-only edge agent**: a long-running process that reads local instruments, stores results durably, and pushes them to the cloud's REST API as an HTTP *client*, never a server. Keep this in mind through everything below — "the gateway" is not something other systems call into.

**Major dependencies/external systems — CONFIRMED BY CODE:**
- **SQLite** via `node:sqlite` (local, file-based, no server) — the only "database."
- **The cloud API** (`apps/api`, a separate NestJS service elsewhere in this monorepo) — reached over HTTPS/HTTP via `fetch`, at two endpoints: `POST /auth/login` and `POST /reception`.
- **WinSW** (Windows Service Wrapper) — hosts the compiled process as a Windows service. Not a code dependency; a deployment-time wrapper (see §2.1).
- **No database driver, no message queue, no MQTT, no serial/USB/Bluetooth library.** `transport/transport.types.ts` and `parser/parser.types.ts` define interfaces for a future byte-level device protocol, but **no concrete implementation of either exists** — INFERRED reason (stated explicitly in code comments): real hardware protocol details (baud rate, framing, Bluetooth profile) are not yet known, and the project's rules forbid inventing them.

**Background workers/schedulers — CONFIRMED BY CODE:** exactly one — `SqliteSyncEngine`'s `setInterval` polling loop (default every 15s), started by `Gateway.start()` and stopped by `Gateway.stop()`.

---

## 1. SYSTEM MAP — WHO TALKS TO WHO?

### 1.1 System Map Diagram

```text
                                   ┌───────────────────────────┐
                                   │   Cloud API (apps/api)   │
                                   │  POST /auth/login         │
                                   │  POST /reception          │
                                   └─────────────▲─────────────┘
                                                 │ HTTPS (fetch)
                                                 │ JSON: credentials, then
                                                 │ {localIdempotencyKey, centreId,
                                                 │  sourceId, vehicleId, quantityKg,
                                                 │  fat, snf, temperature}
                                                 │ Bearer JWT
                        ┌────────────────────────┴─────────────────────────┐
                        │                    GATEWAY PROCESS               │
                        │                  (src/main.ts → Gateway)         │
                        │                                                  │
                        │   ┌────────────┐   start/stop   ┌─────────────┐  │
                        │   │HealthService│◄──────────────┤   Gateway    │  │
                        │   └────────────┘   snapshot      │ (gateway.ts) │  │
                        │                                  └──────┬──────┘  │
                        │              ┌────────────────┬─────────┼─────────────────┐
                        │              ▼                ▼         ▼                 │
                        │      ┌──────────────┐ ┌───────────────┐ ┌───────────────┐  │
                        │      │DeviceManager │ │ LocalStorage  │ │  SyncEngine   │  │
                        │      │(0 devices    │ │(SqliteLocal-  │ │(SqliteSync-   │  │
                        │      │ registered   │ │ Storage)      │ │ Engine)       │  │
                        │      │ at runtime)  │ │               │ │  15s poll     │  │
                        │      └──────┬───────┘ └───────┬───────┘ └───────┬───────┘  │
                        │             │ (interface only, │ SQL             │ calls    │
                        │             │  nothing wired    │                │ CloudClient
                        │             │  in yet)          ▼                ▼          │
                        │             │          ┌─────────────────┐ ┌───────────────┐│
                        │             │          │  gateway.sqlite │ │HttpCloudClient││
                        │             │          │  (WAL mode)     │ │ + auth cache  ││
                        │             │          └─────────────────┘ └───────┬───────┘│
                        │             │                                     │         │
                        └─────────────┼─────────────────────────────────────┼─────────┘
                                      │                                     │
                            (exists only in tests /                (goes to Cloud API,
                             not called from Gateway.start())        top of diagram)
                                      │
                                      ▼
                       ┌───────────────────────────────┐
                       │  ReceptionCaptureWorkflow      │   ← tested in isolation,
                       │  (src/capture/*.ts)            │     NOT invoked by any
                       └───────────────┬────────────────┘     runtime code path
                                       │ Device.read()
                                       ▼
                       ┌───────────────────────────────┐
                       │ WeighingScaleSimulator /       │   ← in-process objects,
                       │ MilkAnalyserSimulator          │     no physical device,
                       │ (src/device/simulators/*.ts)   │     no Transport/Parser
                       └───────────────────────────────┘
```

### 1.2 Data Flow (plain English)

There are really **two separate data flows** in this codebase, and the single most important architectural fact about this gateway is that **only one of them is actually wired together and running**.

**Flow A — capture (proven correct, but not automatically triggered — INFERRED/CONFIRMED gap):**
A `ReceptionCaptureWorkflow` reads a scale device and an analyser device concurrently, validates the combined values against rules that mirror the cloud API's own DTO validation, stamps a fresh idempotency key, and writes the result into SQLite as a paired `local_transactions` row + `outbox_records` row. **This entire flow exists, is unit-tested end-to-end, and works** — but nothing in `Gateway.start()` or `main.ts` ever constructs a `ReceptionCaptureWorkflow` or calls it. The only devices that exist (`WeighingScaleSimulator`, `MilkAnalyserSimulator`) are never registered with `DeviceManager` in production code either. **CONFIRMED BY CODE** (verified: zero references to `ReceptionCaptureWorkflow`, `WeighingScaleSimulator`, or `.register(` anywhere under `src/`) and **CONFIRMED BY THE PROJECT'S OWN README**, which lists this explicitly under "What this checkpoint does NOT include": *"No Gateway/main.ts wiring that automatically invokes ReceptionCaptureWorkflow... an actual operator-facing trigger is a later checkpoint's job."*

**Flow B — sync (the one flow that actually runs unattended):**
Every 15 seconds, `SqliteSyncEngine.tick()` asks `LocalStorage` for outbox rows that are `PENDING` and due (`next_attempt_at <= now`), claims one at a time, and hands each to `HttpCloudClient.sendReception()`. That client obtains (and caches) a JWT by logging into the cloud API, POSTs the reception payload to `/reception`, classifies the HTTP response, and reports back `created` / `duplicate` / `retryable-error` / `terminal-error`. `SyncEngine` writes that outcome back into SQLite: success stamps the cloud's transaction id and marks the row `SYNCED`; a retryable failure bumps the attempt count and reschedules with exponential backoff; a terminal failure (or exceeding 10 attempts) marks the row `FAILED` permanently. **This is the flow that is genuinely automatic** — `Gateway.start()` calls `syncEngine.start()`, which is all it takes to have this loop running for the life of the process.

So today, in a live-running gateway, rows only ever enter `local_transactions`/`outbox_records` if something *other than the gateway's own automatic wiring* calls `ReceptionCaptureWorkflow` or `LocalStorage.createLocalTransaction()` directly (e.g. a test, or a future checkpoint's trigger). Once a row exists there, Flow B picks it up and drives it to the cloud with no further human action. This is the clearest lens for understanding the whole codebase: **the sync/outbox half is a finished product; the capture/device half is a finished, tested component waiting for something to call it.**

### 1.3 Where Does a Request/Event Enter the Gateway?

There is no network listener, so "entering the gateway" means one of these:

```text
OS signal (SIGINT/SIGTERM)
    ↓
process.on() handler in main.ts
    ↓
Gateway.stop()
```

```text
Operating-system process start (WinSW, or a human running `node dist/main.js`)
    ↓
main.ts's main()
    ↓
Gateway.start()
```

```text
setInterval tick (every pollIntervalMs, internally generated — not external)
    ↓
SqliteSyncEngine.tick()
```

```text
CLI flag --status / --version
    ↓
main.ts's diagnosticMode branch
    ↓
Gateway.start() → Gateway.getHealthSnapshot() → stdout JSON → Gateway.stop()
```

**UNKNOWN FROM CURRENT CODE / not yet built:** any operator-facing trigger for a capture (button, poll loop, hardware interrupt). Nothing in this repository defines one yet.

### 1.4 Where Does It Go? (Real Call Chains)

**Startup:**
```text
main.ts: main()
  ↓ loadConfig(resolveConfigPath())
  ↓ new Gateway(config, version, logger)
  ↓ gateway.start()
        ↓ storage.init()                 (SqliteLocalStorage — opens/migrates gateway.sqlite)
        ↓ devices.connectAll()           (DeviceManager — iterates 0 registered devices, no-op)
        ↓ syncEngine.start()             (SqliteSyncEngine — starts the 15s setInterval)
        ↓ health.markStarted() / setServiceState("RUNNING")
```

**Every 15 seconds, automatically, for the life of the process:**
```text
SqliteSyncEngine (setInterval callback)
  ↓ tick()
      ↓ storage.recoverStaleProcessing(now, 5min)         (SqliteLocalStorage)
      ↓ storage.findEligibleOutboxItems(now, batchSize=10)  (SqliteLocalStorage → SELECT ... WHERE status='PENDING' AND next_attempt_at<=now)
      ↓ for each item: processOne(item)
            ↓ storage.claimOutboxItem(id, now)              (UPDATE ... SET status='PROCESSING' WHERE status='PENDING')
            ↓ storage.getLocalTransactionById(...)
            ↓ cloudClient.sendReception(payload)             (HttpCloudClient)
                  ↓ tokenCache.getToken()                    (CloudAuthTokenCache — may POST /auth/login)
                  ↓ fetchWithTimeout(baseUrl + "/reception", POST, Bearer token)
                  ↓ classifyHttpResponse(status, body)        (http-response-classification.ts)
            ↓ storage.markOutboxSynced(...) / markOutboxRetry(...) / markOutboxFailed(...)
```

**Shutdown:**
```text
SIGINT/SIGTERM
  ↓ main.ts shutdown()
      ↓ gateway.stop()
            ↓ syncEngine.stop()      (clearInterval)
            ↓ devices.disconnectAll()
            ↓ storage.close()        (SQLite WAL checkpoint on close)
      ↓ process.exit(0)
```

**What is proven to work, but is not on any of the chains above (capture path):**
```text
ReceptionCaptureWorkflow.captureReception(context)
  ↓ readAndAssemble(context)
        ↓ Promise.all([scale.read(), analyser.read()])   (Device<TReading>.read())
        ↓ assembleReception({...})                        (reception-validation.ts — throws InvalidReadingError on bad data)
        ↓ generateLocalIdempotencyKey(gatewayId)           (storage/idempotency.ts)
  ↓ persist(input)
        ↓ storage.createLocalTransaction(input)            (SqliteLocalStorage — atomic INSERT into local_transactions + outbox_records)
```
This chain is only ever driven by test code (`test/capture/reception-capture-workflow.spec.ts`) today.

### 1.5 Where Does Data Get Stored?

**Only one persistence mechanism exists: a single SQLite file, `gateway.sqlite`, at `<dataDirectory>/gateway.sqlite`.** No other database, cache, queue, or local file store is used for business data. (Structured logs are written to stdout, not to any file the gateway itself manages — see §1.6/§7.)

Schema, **CONFIRMED BY CODE** (`src/storage/schema.ts`, migration version 1, "initial_schema" — this is the entire schema; no migration 2+ exists):

| Table | Purpose | Written by | Read by | Deleted? | Survives restart? |
|---|---|---|---|---|---|
| `schema_migrations` | Tracks which migrations have run | `runMigrations()` at every `init()` | `runMigrations()` (to decide what's pending) | Never | Yes |
| `gateway_metadata` | Single pinned row: this file's owning `gateway_id`/`centre_id` | `initGatewayMetadata()`, once, on first `init()` | Same function, every `init()`, to detect a mismatch | Never | Yes |
| `local_transactions` | One row per assembled milk reception captured at this centre | `createLocalTransaction()` | Capture/sync code, health snapshot (indirectly), tests | Never (rows are permanent even after sync) | Yes |
| `outbox_records` | 1:1 with `local_transactions`; tracks the *attempt* to sync each one to the cloud | `createLocalTransaction()` (insert), `SqliteSyncEngine` (status updates) | `SqliteSyncEngine` every tick | Never | Yes |

**Why it's stored:** `local_transactions` is the durable, local record of a completed reception the moment it's captured — so it exists and is safe even if the cloud is unreachable for hours. `outbox_records` is deliberately a *separate* concept from "is this synced" (that's `local_transactions.cloud_transaction_id IS NOT NULL`) — it exists purely to track sync *attempts*: status, retry count, next-attempt time, last error, and a claim timestamp used for crash recovery.

**Outbox state machine — CONFIRMED BY CODE:**
```text
        create                 tick claims it            cloud says OK
PENDING ────────► (initial)  PENDING ──────► PROCESSING ──────────► SYNCED (terminal)
   ▲                                              │
   │ retryable failure (backoff)                  │ terminal failure / max attempts (10)
   └──────────────────────────────────────────────┴──────────────► FAILED (terminal)

Also: PROCESSING rows found stuck (crash) are swept back to PENDING —
   at startup (threshold 0 — ANY stuck row is orphaned) and
   every tick (threshold 5 min — catches an in-process hang).
```

`FAILED` rows are never deleted or retried automatically — **CONFIRMED BY CODE** comment: "requires manual/ops intervention... nothing here is silently lost." **UNKNOWN FROM CURRENT CODE:** no tool exists yet to requeue a `FAILED` row (the README confirms this is intentionally not built yet).

**In-memory-only state (not persisted, does not survive restart) — CONFIRMED BY CODE:** `HealthService`'s entire snapshot (service state, uptime, cloud/device connectivity, cached pending count) lives only in process memory; it is deliberately not written to SQLite, since it is "a live runtime fact... not a durable record."

---

## 1.6 What Happens When Something Fails?

**Device unavailable/disconnects:**
```text
Device.read() rejects (SimulatedDeviceBase throws if not CONNECTED,
or a queued failure/goOffline() forces state to ERROR)
     ↓
ReceptionCaptureWorkflow.readAndAssemble() rejects, propagating the error unchanged
     ↓
No idempotency key is ever generated, nothing is written to SQLite — CONFIRMED BY CODE
     ↓
No retry logic exists for this at all — it is entirely the caller's problem.
```
**INFERRED / gap:** because nothing currently calls `readAndAssemble()`/`captureReception()` at runtime, this failure path is exercised only by tests today, not by a live process.

**Cloud API unavailable:**
```text
fetch() throws (network error / DNS / timeout via AbortController)
     ↓
HttpCloudClient catches it, returns {outcome: "retryable-error", message}  — never a thrown exception up to SyncEngine
     ↓
SqliteSyncEngine.handleFailure(): storage.markOutboxRetry()
     ↓
attemptCount++, nextAttemptAt = now + backoff (1s, 2s, 4s, 8s... capped at 5 min, deterministic, no jitter)
     ↓
Row stays PENDING; nothing is lost. Picked up again once nextAttemptAt passes.
     ↓
After 10 total attempts, markOutboxFailed() — terminal, ops must intervene.
```
A `401` gets one automatic retry after invalidating the cached token (re-login); a second `401` in a row is treated as terminal (bad credentials), not retried forever. A `409` (idempotency payload conflict) and any other non-retryable 4xx are terminal immediately — retrying an identical bad request can never succeed.

**Database (SQLite) failure:**
`init()` failures (bad path, permissions, corrupt file) close the half-open connection and re-throw — **CONFIRMED BY CODE**, `Gateway.start()` does not catch this, so it propagates up through `main.ts`'s `main()` and crashes the process (uncaught rejection handler prints the stack and `process.exit(1)`). **INFERRED:** there is no runtime "SQLite write failed mid-operation" recovery beyond the transaction rollback (`withTransaction`'s `ROLLBACK` on any thrown error) — a failure during a write throws normally and the caller (e.g. `SyncEngine.processOne`) does not specifically special-case a storage-layer exception the way it special-cases cloud-client exceptions.

**Gateway process crash:**
```text
Node process dies (any reason) — no application-level guard against this beyond
graceful SIGINT/SIGTERM handling
     ↓
WinSW (Windows Service Wrapper) observes the child exited
     ↓
<onfailure action="restart" delay="5 sec"/> in CCMCGateway.xml — CONFIRMED BY CODE (the XML file)
     ↓
New process starts → main.ts → Gateway.start() → storage.init()
     ↓
recoverStaleProcessing(now, staleThresholdMs=0) sweeps every PROCESSING row back to PENDING,
since a fresh process has made no claims yet — anything mid-flight when the crash happened
is safely retried, never lost, never double-counted (idempotency key + cloud-side dedup cover
the case where the cloud actually received it just before the crash).
```

---

## 2. GATEWAY LIFECYCLE

**True entrypoint — CONFIRMED BY CODE:** `src/main.ts` (compiled: `dist/main.js`). This is exactly what `package.json`'s `start` script runs and exactly what WinSW's `<executable>`/`<arguments>` launches.

**First function that runs:** `main()` in `main.ts`, invoked at the bottom of the file and wrapped in a top-level `.catch()` that prints a stack trace and exits 1 on any unhandled failure.

**Initialization order — CONFIRMED BY CODE:**

```text
1. Read own version from package.json (readOwnVersion())
2. Resolve config path (GATEWAY_CONFIG_PATH env var, else cwd/config/gateway.config.json)
3. loadConfig() — parse + validate JSON; ConfigValidationError → print to stderr, exit 1
4. Construct Logger("gateway")
5. Construct Gateway(config, version, logger)
       — this also constructs, but does NOT yet start:
         HealthService, DeviceManager (empty), SqliteLocalStorage,
         HttpCloudClient (wraps a CloudAuthTokenCache), SqliteSyncEngine
6. gateway.start():
       a. health.setServiceState("STARTING")
       b. storage.init()        — opens SQLite, runs migrations, pins gateway/centre identity,
                                    sweeps any stale PROCESSING rows from a prior crash
       c. devices.connectAll()  — connects every registered device (currently: none)
       d. syncEngine.start()    — begins the 15s poll loop
       e. health.markStarted() / setServiceState("RUNNING")
       f. refreshPendingSyncCount() — one real COUNT(*) query against the outbox
7. (normal mode) register SIGINT/SIGTERM handlers, then hold the event loop open
   with a dummy setInterval(() => {}, ~12.4 days) since there is no server/listener
   to do this implicitly
   — OR —
   (diagnostic mode, --status/--version) print one JSON health snapshot to stdout,
   call gateway.stop(), and exit immediately
```

**What keeps the process alive:** in normal mode, nothing but that dummy `setInterval` — **CONFIRMED BY CODE comment**, explicitly because "there is no server/listener yet to do this implicitly (Checkpoint 2 has no HTTP server)." The *actual* recurring work is `SqliteSyncEngine`'s own separate `setInterval`, which would itself keep Node alive even without the dummy one.

**What remains running while the process is up:** exactly one timer — `SqliteSyncEngine`'s poll loop. Nothing else (no listeners, no other workers).

**Shutdown sequence — CONFIRMED BY CODE:**
```text
SIGINT or SIGTERM
  ↓ shutdown(signal) — idempotent guard (shuttingDown flag) against a double-invoke
  ↓ clearInterval(keepAlive)
  ↓ logger.info("Received shutdown signal")
  ↓ gateway.stop()
        syncEngine.stop()       — clearInterval
        devices.disconnectAll() — disconnects any registered devices
        storage.close()         — db.close() → WAL checkpoint happens here
        health.setServiceState("STOPPED") / clearStarted()
  ↓ process.exit(0) on success, or exit(1) + logged error if stop() itself throws
```

### 2.1 Windows Service Layer

This is a genuinely separate concern from the application code above, and the codebase keeps them cleanly separated:

**Hosting layer (deployment-time, no gateway TypeScript involved) — CONFIRMED BY CODE (`service/CCMCGateway.xml`, `install.ps1`):**
```text
Windows boots
   ↓
SCM (Service Control Manager) — <startmode>Automatic</startmode>, no user logon required
   ↓
WinSW (renamed to CCMCGateway.exe) — reads CCMCGateway.xml
   ↓
Launches: node\node.exe app\dist\main.js  (a vendored, pinned Node runtime — not a system-wide Node install)
   ↓
Working directory = install root (%BASE%), so main.ts's default relative config path
("./config/gateway.config.json") resolves correctly with zero Windows-specific
path logic in the application
```

**Application layer (what the gateway's own code does, independent of how it was launched):** everything in §2's lifecycle above — config loading, SQLite, device connect, sync loop, SIGINT/SIGTERM handling.

**"WinSW does X, but not Y":**
- WinSW **does** start the process automatically on boot, restart it on any crash (`<onfailure action="restart" delay="5 sec"/>`, reset after 1 day clean), and capture the raw stdout/stderr into its own rolling log files (`<log mode="roll-by-size">`, 10MB per file, 8 kept).
- WinSW does **not** know anything about SQLite, outbox rows, idempotency, the cloud API, or devices. It only ever sees "is this Windows process alive or not" — all crash-*recovery-of-data* (as opposed to crash-recovery-of-process) is the application's own job, via `recoverStaleProcessing()` at startup.
- WinSW does **not** parse or understand the gateway's own structured JSON log lines — it just redirects the raw stream into files. The gateway's `Logger` (§4.1) writes those JSON lines to stdout specifically so that WinSW's dumb byte-redirection is enough to get them into a log file with zero extra wiring.
- The SCM's own "Recovery" tab actions are **deliberately not configured** — **CONFIRMED BY CODE comment**: a second supervisor watching the first supervisor (WinSW) was judged unneeded complexity, "unless real-world Windows testing turns up a concrete gap."

**Explicitly not verified — CONFIRMED BY CODE COMMENT, stated plainly in the XML file's own header:** none of this has been run against a real Windows SCM. The sandbox this was built in is Linux-only; `CCMCGateway.exe` and `node\node.exe` in a built deployment folder are labeled placeholder files (network egress to GitHub/nodejs.org was blocked — `packaging/build-deployment.mjs`'s header documents the exact 403s). This is real, reviewed configuration, not yet a *proven* one.

**If this differs from an earlier "Windows Service hosting report":** the WinSW config here matches the description above (Automatic start, WinSW-level restart-on-failure as the sole recovery mechanism, LocalSystem account left as a Phase-12 TBD, binaries/data kept in separate folder branches so an app upgrade never touches `data\`/`logs\`). If your earlier report said something different — e.g. assumed a least-privilege service account, or SCM-level recovery actions — that part of the report describes an intended *future* state (Phase 12), not what's implemented in this file today.

---

## 3. TRACE ONE REAL REQUEST END-TO-END

The most representative **fully-wired, automatically-running** flow in this codebase is a sync tick delivering one outbox row to the cloud. (The capture flow in §1.2/§1.4 is equally real and equally tested, but is not automatically triggered today, so it is traced separately below as "the other half.")

### 3.1 The sync tick — call chain

```text
setInterval (started by Gateway.start() → syncEngine.start())
  ↓
SqliteSyncEngine.tick()                              [src/sync/sync-engine.ts]
  ↓
storage.findEligibleOutboxItems(now, 10)             [SqliteLocalStorage — SELECT ... status='PENDING']
  ↓
SqliteSyncEngine.processOne(item)
  ↓
storage.claimOutboxItem(item.id, now)                [UPDATE ... status='PROCESSING' WHERE status='PENDING']
  ↓
storage.getLocalTransactionById(item.localTransactionId)
  ↓
HttpCloudClient.sendReception(payload)                [src/sync/http-cloud-client.ts]
  ↓
CloudAuthTokenCache.getToken()                        [src/sync/cloud-auth.ts]
  │     (if expired/absent) → login() → POST {baseUrl}/auth/login → decode JWT `exp` claim, cache it
  ↓
fetchWithTimeout({baseUrl}/reception, POST, Authorization: Bearer <token>, JSON body)
  ↓
classifyHttpResponse(status, body)                    [src/sync/http-response-classification.ts]
  ↓
back in SyncEngine.processOne(): storage.markOutboxSynced(...) / markOutboxRetry(...) / markOutboxFailed(...)
```

### 3.2 Step-by-step

| Step | File / Class / Method | Responsibility | Input | Output | Next hop |
|---|---|---|---|---|---|
| 1 | `sync-engine.ts` / `SqliteSyncEngine.tick()` | Bound the amount of work done per timer fire; recover any stuck rows first | none (reads system clock) | up to 10 eligible `OutboxRecord`s | `processOne()` per item |
| 2 | `sqlite-local-storage.ts` / `findEligibleOutboxItems` | Query which rows are due for a retry | `nowIso`, `limit` | `OutboxRecord[]` (status PENDING, due) | back to `tick()` |
| 3 | `sqlite-local-storage.ts` / `claimOutboxItem` | Atomically flip one row to PROCESSING so no other tick reprocesses it | `id`, `nowIso` | `boolean` (false if already claimed) | `processOne()` continues only if true |
| 4 | `sqlite-local-storage.ts` / `getLocalTransactionById` | Fetch the actual business data for this outbox row | `localTransactionId` | `LocalTransaction \| null` | `toPayload()` |
| 5 | `sync-engine.ts` / `toPayload()` | Map internal shape → wire shape the cloud API expects | `LocalTransaction`, key | `ReceptionSyncPayload` | `cloudClient.sendReception()` |
| 6 | `http-cloud-client.ts` / `HttpCloudClient.sendReception` → `attempt()` | Obtain a valid auth token, POST the payload, interpret the result | `ReceptionSyncPayload` | `CloudSendResult` | back to `processOne()` |
| 6a | `cloud-auth.ts` / `CloudAuthTokenCache.getToken` | Return a cached, still-valid JWT, or fetch a new one | none | `string` (JWT) | `attempt()`'s fetch call |
| 6b | `http-cloud-client.ts` / `login()` (private) | POST credentials to the cloud, extract `accessToken` | `email`, `password` | `{accessToken}` | `doRefresh()` caches it |
| 6c | `http-response-classification.ts` / `classifyHttpResponse` | Turn an HTTP status + JSON body into one of 4 clean outcomes | `status`, `body` | `ClassifiedResponse` | `attempt()`'s switch |
| 7 | `sync-engine.ts` / `processOne()`'s switch on `result.outcome` | Apply the outcome durably | `CloudSendResult` | SQLite writes | `markOutboxSynced` / `markOutboxRetry` / `markOutboxFailed` |
| 8 | `sqlite-local-storage.ts` / `markOutboxSynced` | Stamp the cloud's id, close out this row as SYNCED | `id`, `cloudTransactionId`, `nowIso` | DB rows updated | (terminal — success) |

### 3.3 In human language

*A timer inside `SqliteSyncEngine` fires every 15 seconds — nothing external triggers it, it's just the gateway checking in on itself. It asks `LocalStorage` (the SQLite wrapper) for outbox rows that are waiting and due for an attempt. `LocalStorage` owns the actual SQL and hands back plain objects — `SyncEngine` never writes a query itself, and it never talks HTTP either. For each due row, `SyncEngine` first "claims" it (an atomic UPDATE that flips its status to PROCESSING) so a slow tick can never double-process the same row. Then it asks `HttpCloudClient` to actually deliver it. `HttpCloudClient` is the only piece of code in the entire gateway that knows what an HTTP request looks like — it first makes sure it has a valid login token (asking `CloudAuthTokenCache`, which itself will silently re-login if the cached token expired or doesn't exist yet), then POSTs the reception data to the cloud's `/reception` endpoint with that token in the `Authorization` header. Whatever HTTP status and body comes back gets handed to a small, pure function — `classifyHttpResponse` — whose only job is to turn "the internet's opinion" into one of four outcomes the rest of the system actually understands: created, duplicate-but-fine, retry-me-later, or give-up. `SyncEngine` never has to know what a 409 or a 5xx even means; it just switches on that clean outcome and tells `LocalStorage` to mark the row synced, reschedule it with backoff, or fail it permanently. That's the whole loop, and it repeats forever while the process is alive.*

---

## 4. THE FILES

### 4.1 File Table

| File | Role | Talks To | Called By | Why It Exists |
|---|---|---|---|---|
| `src/main.ts` | Process entrypoint; loads config, owns process lifecycle & signals | `config.loader`, `Gateway`, `Logger` | OS / WinSW / `node dist/main.js` | The one place the OS actually launches |
| `src/gateway.ts` | Orchestrates start/stop order of every subsystem | `HealthService`, `DeviceManager`, `LocalStorage`, `SyncEngine` | `main.ts` | Single seam that owns lifecycle ordering, so nothing else has to |
| `src/config/config.loader.ts` | Parses & fail-fast-validates the JSON config file | filesystem | `main.ts` | Guarantees no `undefined` config value silently flows through the app |
| `src/config/config.types.ts` | Shape of `GatewayConfig` | — (types only) | everything that takes config | Single source of truth for what a deployment must supply |
| `src/device/device.types.ts` | `Device<TReading>` interface | — (types only) | `DeviceManager`, simulators, `ReceptionCaptureWorkflow` | The seam any future real hardware adapter implements |
| `src/device/device-manager.ts` | Tracks registered devices as a group; connect/disconnect all | `Device` instances | `Gateway` | Central place to bulk-manage device lifecycle — currently manages zero |
| `src/device/simulators/*.ts` | In-process fake `Device` implementations for demos/tests | `SimulatedDeviceBase` | tests only (not `src/`) | Lets the capture/storage/sync pipeline be built and proven before real hardware exists |
| `src/transport/transport.types.ts` | Byte-channel interface (serial/BT/TCP) | — (types only, unimplemented) | nothing yet | Reserved seam for real hardware; deliberately not filled in |
| `src/parser/parser.types.ts` | Byte→reading parser interface | — (types only, unimplemented) | nothing yet | Reserved seam for real hardware framing/parsing |
| `src/normalization/normalized-reading.types.ts` | `ScaleReading` / `AnalyserReading` shapes | — (types only) | devices, capture, validation | The stable shape everything above the device layer works with |
| `src/capture/reception-capture-workflow.ts` | Reads devices, assembles, keys, persists one reception | `Device`, `reception-validation`, `LocalStorage`, `idempotency` | tests only (not wired into `Gateway`) | The intended "application logic" seam for turning readings into a transaction |
| `src/capture/reception-validation.ts` | Validates & assembles a raw reading pair into a reception | — (pure function) | `ReceptionCaptureWorkflow` | Mirrors the cloud API's own DTO validation exactly, so the gateway never sends what the cloud would reject |
| `src/storage/storage.types.ts` | `LocalStorage` interface | — (types only) | `Gateway`, `SyncEngine`, `ReceptionCaptureWorkflow` | Decouples all callers from SQLite specifically |
| `src/storage/sqlite-local-storage.ts` | The real SQLite-backed implementation | `node:sqlite`, `schema.ts`, `backoff.ts` | `Gateway` (constructs it by default) | All persistence logic — migrations, atomicity, idempotency conflict detection, outbox state transitions |
| `src/storage/schema.ts` | Migration framework + the one real migration | `node:sqlite` | `SqliteLocalStorage.init()` | Ensures schema upgrades are ordered, tracked, and idempotent |
| `src/storage/local-transaction.types.ts` | `LocalTransaction`/`OutboxRecord` shapes + state enum | — (types only) | storage, sync, capture | Documents the outbox state machine in one place |
| `src/storage/idempotency.ts` | Generates the one-time local idempotency key | `crypto.randomUUID` | `ReceptionCaptureWorkflow` | Guarantees a retried capture never becomes a duplicate transaction |
| `src/storage/backoff.ts` | Deterministic exponential backoff calculation | — (pure function) | `SqliteLocalStorage.markOutboxRetry` | Makes retry timing exactly reproducible/testable |
| `src/storage/errors.ts` | `GatewayIdentityMismatchError`, `IdempotencyKeyConflictError` | — | `SqliteLocalStorage` | Makes two specific data-integrity failures loud and typed instead of silent |
| `src/sync/sync.types.ts` | `SyncEngine` interface | — (types only) | `Gateway` | Decouples `Gateway` from the concrete sync implementation |
| `src/sync/sync-engine.ts` | Polls outbox, drives each item through `CloudClient`, applies outcome | `LocalStorage`, `CloudClient` | `Gateway` (constructs by default) | The offline-tolerant delivery loop; contains zero HTTP code itself |
| `src/sync/cloud-client.types.ts` | `CloudClient` interface + wire payload/result shapes | — (types only) | `SyncEngine`, `HttpCloudClient`, test fakes | Lets `SyncEngine` be fully tested against a fake before any real HTTP existed |
| `src/sync/http-cloud-client.ts` | Real HTTP implementation of `CloudClient` | `CloudAuthTokenCache`, `classifyHttpResponse`, `fetch` | `Gateway` (constructs by default) | The only file in the gateway that knows HTTP/fetch exists |
| `src/sync/cloud-auth.ts` | Caches/refreshes the cloud JWT | `login` callback (injected) | `HttpCloudClient` | Keeps "do I need to log in again" logic isolated and testable |
| `src/sync/http-response-classification.ts` | Pure status+body → outcome mapping | — (pure function) | `HttpCloudClient` | Makes every retry/terminal rule independently unit-testable, no network needed |
| `src/health/health.service.ts` | In-memory service-state/health tracker | — | `Gateway` | Answers "what is this process doing right now" for `--status` and logs |
| `src/health/health.types.ts` | Health snapshot shape | — (types only) | `HealthService` | Documents exactly which fields are real vs. placeholder |
| `src/logging/logger.ts` | Minimal JSON-line stdout logger | `process.stdout` | everywhere | Cheap, structured, WinSW-redirectable logging with no dependency |
| `config/gateway.config.example.json` | Template config | — | operators (copy → edit) | Documents the exact required shape without shipping real secrets |
| `service/CCMCGateway.xml`, `install.ps1`, `uninstall.ps1` | WinSW service registration | Windows SCM (deployment-time) | an operator, once, at install time | Hosts the compiled process as an unattended Windows service |
| `packaging/build-deployment.mjs` | Assembles the Windows deployment folder | filesystem, `tsc` | operator/CI, manually | Proves binaries/runtime stay separate from persistent data on disk |

### 4.2 Grouped by Architectural Layer

```text
ENTRYPOINT
│
├── src/main.ts                        (process bootstrap, signal handling)
└── src/gateway.ts                     (subsystem orchestration)

DEVICE / HARDWARE (no HTTP/API layer exists in this codebase — see below)
│
├── src/device/device.types.ts
├── src/device/device-manager.ts
├── src/device/simulators/*.ts         (only concrete Device implementations that exist)
├── src/transport/transport.types.ts   (seam only, unimplemented)
└── src/parser/parser.types.ts         (seam only, unimplemented)

BUSINESS LOGIC / APPLICATION WORKFLOW
│
├── src/normalization/normalized-reading.types.ts
├── src/capture/reception-capture-workflow.ts   (built, tested, NOT wired into runtime)
└── src/capture/reception-validation.ts

PERSISTENCE
│
├── src/storage/storage.types.ts
├── src/storage/sqlite-local-storage.ts
├── src/storage/schema.ts
├── src/storage/local-transaction.types.ts
├── src/storage/idempotency.ts
├── src/storage/backoff.ts
└── src/storage/errors.ts

CLOUD SYNC (the gateway's ONLY outbound "API client" layer)
│
├── src/sync/sync.types.ts
├── src/sync/sync-engine.ts
├── src/sync/cloud-client.types.ts
├── src/sync/http-cloud-client.ts
├── src/sync/cloud-auth.ts
└── src/sync/http-response-classification.ts

OBSERVABILITY / CONFIGURATION
│
├── src/health/health.service.ts
├── src/health/health.types.ts
├── src/logging/logger.ts
├── src/config/config.loader.ts
└── src/config/config.types.ts

DEPLOYMENT (not application code)
│
├── service/CCMCGateway.xml, install.ps1, uninstall.ps1
└── packaging/build-deployment.mjs
```

There is deliberately no "HTTP/API" folder the way a typical inbound gateway would have one — **CONFIRMED BY CODE**: this gateway never listens for a connection; `src/sync/` is the closest analogue, and it is entirely an outbound HTTP *client*.

### 4.3 Tier-1 File-by-File (the files that matter most)

```text
File:
    src/gateway.ts
Architectural layer:
    Entrypoint / orchestration
Purpose:
    Owns the exact start/stop order for every subsystem, and constructs
    real defaults (SqliteLocalStorage, HttpCloudClient, SqliteSyncEngine)
    while still allowing a caller (tests) to inject fakes instead.
Who calls it:
    src/main.ts (constructs one Gateway, calls start()/stop()/getHealthSnapshot())
Who it calls:
    HealthService, DeviceManager, LocalStorage, SyncEngine
What data enters:
    GatewayConfig, version string, optional injected Logger/Storage/SyncEngine
What data leaves:
    GatewayHealthSnapshot (via getHealthSnapshot())
Protocol/system involved:
    None directly — pure orchestration
Why this file exists:
    Without it, main.ts would have to know the correct init order of four
    unrelated subsystems itself, and every test would have to reconstruct
    that order by hand.
What would break if removed:
    Nothing could start at all — this is the one class that wires
    everything else together.
```

```text
File:
    src/storage/sqlite-local-storage.ts
Architectural layer:
    Persistence
Purpose:
    The only implementation of LocalStorage. Owns the SQLite connection,
    migrations, the gateway/centre identity safeguard, and every durable
    read/write the rest of the gateway needs, including the outbox state
    machine transitions.
Who calls it:
    Gateway (constructs the default instance), SyncEngine, ReceptionCaptureWorkflow (via the LocalStorage interface)
Who it calls:
    node:sqlite (DatabaseSync), src/storage/schema.ts (runMigrations), src/storage/backoff.ts
What data enters:
    CreateLocalTransactionInput, outbox status transitions, queries by id/key
What data leaves:
    LocalTransaction / OutboxRecord objects, pending counts
Protocol/system involved:
    SQLite (file-based, WAL mode, synchronous=FULL, foreign_keys=ON)
Why this file exists:
    Concentrates every SQL statement and every atomicity/idempotency
    guarantee behind one interface, so nothing else in the gateway needs
    to know SQL exists.
What would break if removed:
    No data would ever be durable across a restart — captured receptions
    and sync progress would live only in memory and vanish on crash.
```

```text
File:
    src/sync/sync-engine.ts
Architectural layer:
    Cloud sync
Purpose:
    The offline-tolerant delivery loop: claim due outbox rows, hand each
    to a CloudClient, apply the result back to storage, with backoff and
    a max-attempt terminal cutoff. Contains no HTTP/network code at all.
Who calls it:
    Gateway (start()/stop()), its own internal setInterval (tick())
Who it calls:
    LocalStorage (query/claim/mark methods), CloudClient (sendReception)
What data enters:
    Nothing external — reads its own clock and LocalStorage's state
What data leaves:
    Outbox status writes back to LocalStorage
Protocol/system involved:
    None directly (delegates HTTP entirely to CloudClient)
Why this file exists:
    Lets retry/backoff/state-machine correctness be fully unit-tested
    against a fake CloudClient, with zero real network calls, before any
    HTTP code existed (this is literally how the project was built,
    checkpoint by checkpoint).
What would break if removed:
    Captured transactions would accumulate in local_transactions forever
    and never reach the cloud — the "sync" half of "offline-first sync"
    simply wouldn't happen.
```

```text
File:
    src/sync/http-cloud-client.ts
Architectural layer:
    Cloud sync
Purpose:
    The one and only place that makes a real HTTP request. Logs in (via
    CloudAuthTokenCache), POSTs a reception, classifies the response, and
    handles exactly one automatic re-login-and-retry on a 401.
Who calls it:
    SqliteSyncEngine (via the CloudClient interface), Gateway (constructs the default instance)
Who it calls:
    CloudAuthTokenCache, classifyHttpResponse, global fetch
What data enters:
    ReceptionSyncPayload (business data + idempotency key)
What data leaves:
    CloudSendResult (created/duplicate/retryable-error/terminal-error)
Protocol/system involved:
    HTTPS/HTTP via fetch; JSON bodies; Bearer JWT auth
Why this file exists:
    Isolates every literal detail of "how do I talk to this specific
    cloud API" (endpoint paths, auth header format, timeout handling)
    behind the same CloudClient interface the fake test double already
    satisfied — SyncEngine never had to change when this was added.
What would break if removed:
    No path to the cloud would exist at all; Gateway's default
    constructor wiring would have nothing to build a SyncEngine around.
```

```text
File:
    src/capture/reception-capture-workflow.ts
Architectural layer:
    Business logic / application workflow
Purpose:
    Turns "read two devices" into "one durable, idempotent local
    transaction," making two explicit, documented assumptions (concurrent
    reads via Promise.all; capturedAt = assembly time) precisely because
    neither is confirmed by the business requirements or real hardware yet.
Who calls it:
    Nothing in src/ today — only test/capture/reception-capture-workflow.spec.ts
Who it calls:
    Device.read() (x2), reception-validation.assembleReception(), generateLocalIdempotencyKey(), LocalStorage.createLocalTransaction()
What data enters:
    CaptureContext (centreId/sourceId/vehicleId)
What data leaves:
    CreateLocalTransactionResult
Protocol/system involved:
    None directly — pure orchestration over the Device and LocalStorage interfaces
Why this file exists:
    Names and documents an assumption-laden sequencing decision explicitly,
    rather than hiding it inside the Device interface or a simulator, so
    it can be revisited without touching either.
What would break if removed:
    Nothing currently running would break (it isn't called at runtime) —
    but the entire tested proof that Device → validated reception →
    LocalStorage works end-to-end would disappear, and a future trigger
    (button, poll loop) would have nowhere to plug in.
```

### 4.4 Tier 2 — Supporting Files (brief)

`http-response-classification.ts` and `backoff.ts` are small, pure, exhaustively-tested functions extracted specifically so their business rules (which HTTP statuses retry vs. terminate; exact backoff timing) can be asserted on without any I/O. `idempotency.ts` is a five-line wrapper around `crypto.randomUUID()`, prefixed with the gateway id for traceability. `errors.ts` defines two narrow, purpose-built error classes so two specific data-integrity failures (a copied SQLite file from another install; a reused idempotency key with different data) are impossible to mistake for an ordinary bug. `logger.ts` is a deliberately dependency-free JSON-line logger. `health.types.ts` / `sync.types.ts` / `storage.types.ts` / `device.types.ts` / `cloud-client.types.ts` / `transport.types.ts` / `parser.types.ts` / `normalized-reading.types.ts` are all pure TypeScript interfaces with no runtime behavior — they exist to fix a contract before (or instead of) an implementation.

### 4.5 Tier 3 — Tests / Generated / Config (brief)

`test/` mirrors `src/`'s structure one-for-one for unit specs. `test/cloud-integration/` is a second, separate Jest project (its own `jest-cloud.config.js`, run via `pnpm test:cloud`) that spins up a real Postgres-backed cloud API and exercises `HttpCloudClient` against it for real — deliberately excluded from the default `pnpm test` run so that stays hermetic. `test/restart-recovery/` kills a child gateway process mid-write to prove the crash-recovery sweep actually works, not just in theory. `tsconfig.json` targets ES2021/CommonJS and excludes `test/` from the compiled build. `jest.config.js` runs everything under `test/**/*.spec.ts` except `cloud-integration/`. `config/gateway.config.example.json` is a committed placeholder; the real `config/gateway.config.json` is gitignored.

---

## 5. DEPENDENCY MAP

```text
main.ts
 ├── config/config.loader.ts  →  config/config.types.ts
 ├── logging/logger.ts
 └── gateway.ts
      ├── health/health.service.ts       → health/health.types.ts, config/config.types.ts
      ├── device/device-manager.ts       → device/device.types.ts, logging/logger.ts
      ├── storage/sqlite-local-storage.ts
      │     ├── storage/schema.ts
      │     ├── storage/backoff.ts
      │     ├── storage/errors.ts
      │     └── storage/storage.types.ts, storage/local-transaction.types.ts
      └── sync/sync-engine.ts (SqliteSyncEngine)
            ├── storage/storage.types.ts   (imports the INTERFACE only)
            ├── sync/cloud-client.types.ts
            └── sync/http-cloud-client.ts (HttpCloudClient, the concrete default)
                  ├── sync/cloud-auth.ts
                  └── sync/http-response-classification.ts

(not imported by main.ts or gateway.ts at all — only by test files)
capture/reception-capture-workflow.ts
      ├── device/device.types.ts
      ├── normalization/normalized-reading.types.ts
      ├── storage/idempotency.ts
      ├── storage/local-transaction.types.ts, storage/storage.types.ts
      └── capture/reception-validation.ts

device/simulators/*.ts
      └── device/simulators/simulated-device-base.ts → device/device.types.ts
```

**Import dependency vs. runtime communication — these are not the same thing, and this codebase is a good illustration of why:**

- `sync-engine.ts` **imports** `storage.types.ts` (a TypeScript interface) — that's a compile-time-only dependency; it doesn't describe any runtime protocol.
- `sync-engine.ts` **communicates at runtime** with `SqliteLocalStorage` by calling its methods in-process (plain JS function calls — no network, no serialization) — and separately, **communicates at runtime** with the cloud API over real HTTPS, but only indirectly, through `HttpCloudClient`, which it never imports directly (it only imports the `CloudClient` *interface*).
- `Gateway` **imports** `HttpCloudClient` directly (to construct the default one) — but the actual bytes-over-the-wire communication that class performs happens only when `sendReception()` is called at runtime, nowhere near the import statement.
- The clearest case: `capture/reception-capture-workflow.ts` **imports** `LocalStorage` and would **communicate** with SQLite at runtime, exactly the way `Gateway` does — but because nothing in `main.ts`/`gateway.ts` constructs and calls a `ReceptionCaptureWorkflow`, that runtime communication **never actually happens** in a deployed process today, even though the import-level wiring is fully in place. Import graphs alone would make you think this file is "connected" to storage; only tracing actual constructor calls (§1.2) reveals it isn't exercised at runtime yet.

---

## 6. EXTERNAL SYSTEM MAP

| External System | Gateway Component | Protocol | Direction | Purpose |
|---|---|---|---|---|
| Cloud API — `POST /auth/login` | `CloudAuthTokenCache` (via `HttpCloudClient.login()`) | HTTPS (fetch), JSON | Gateway → Cloud | Obtain a JWT for this gateway's service account |
| Cloud API — `POST /reception` | `HttpCloudClient.sendReception()` | HTTPS (fetch), JSON, Bearer auth | Gateway → Cloud | Deliver one captured milk reception |
| Local SQLite file (`gateway.sqlite`) | `SqliteLocalStorage` | `node:sqlite` (in-process, file-based) | Both (gateway reads and writes its own file) | Durable local record of receptions + sync attempt state |
| Windows SCM / WinSW | (none — external to the process) | OS process supervision | Windows → Gateway (start/stop/restart) | Runs the gateway unattended as a background service |
| Local config file (`config/gateway.config.json`) | `config.loader.ts` | filesystem (read-only) | Gateway ← file | Supplies gatewayId/centreId/cloud URL/credentials/paths |
| Physical weighing scale / milk analyser | **NONE — not connected. `Transport`/`Parser` interfaces exist, no implementation does.** | UNKNOWN FROM CURRENT CODE (no baud rate, framing, or Bluetooth profile is defined anywhere) | n/a today | Intended eventual data source; currently replaced entirely by in-process simulators |

---

## 7. CONFIGURATION MAP

**Sources, in order — CONFIRMED BY CODE (`config/config.loader.ts`):**
```text
GATEWAY_CONFIG_PATH environment variable, if set
   ↓ (else)
<current working directory>/config/gateway.config.json
```
There is no command-line-argument configuration besides the two diagnostic flags (`--status`, `--version`) checked directly in `main.ts`. There are no other environment variables read anywhere in `src/` — **CONFIRMED BY CODE** (only `process.env` reference in `src/` is `GATEWAY_CONFIG_PATH`, plus the implicit `process.argv`/`process.stdout`/`process.on`).

| Name | Where defined | Where loaded | Where used | Behavior it controls |
|---|---|---|---|---|
| `gatewayId` | `config/gateway.config.json` (per-deployment) | `config.loader.ts` | `HealthService` (snapshot), `SqliteLocalStorage` (identity pin), `idempotency.ts` (key prefix) | Which installation this process claims to be |
| `centreId` | same | same | `HealthService`, `SqliteLocalStorage` (identity pin) | Which chilling centre this gateway serves; mismatch vs. a copied DB file is fatal |
| `cloudApiBaseUrl` | same | same | `Gateway`'s default `HttpCloudClient` construction | Where `/auth/login` and `/reception` are POSTed |
| `logDirectory` | same | same | **Nowhere else — CONFIRMED BY CODE.** Required at load time (missing it fails config validation) but no runtime code ever reads `config.logDirectory`. `Logger` unconditionally writes to stdout regardless of this value. | Currently: none. (WinSW's own, separately hardcoded `<logpath>%BASE%\logs</logpath>` in `CCMCGateway.xml` is what actually determines where log files land — it is not derived from this config field at all.) |
| `dataDirectory` | same | same | `SqliteLocalStorage` (`path.join(dataDirectory, "gateway.sqlite")`, and `mkdirSync`) | Where the SQLite file lives on disk |
| `cloudAuthEmail` | same | same | `Gateway`'s default `HttpCloudClient` construction | The service-account identity used to log in |
| `cloudAuthPassword` | same [SECRET/REDACTED] | same | same | The service-account credential — never logged anywhere, by explicit design (verified: every log statement in the sync path logs only ids/status/booleans) |
| `pollIntervalMs`, `batchSize`, `maxAttempts`, `staleProcessingThresholdMs` | `SyncEngineOptions`, hardcoded default constants in `sync-engine.ts` (**not** exposed in `gateway.config.json`) | `DEFAULT_SYNC_ENGINE_OPTIONS` | `SqliteSyncEngine` | Tick frequency (15s), items per tick (10), retry ceiling (10 attempts), stale-claim recovery window (5 min) |
| `requestTimeoutMs` | `HttpCloudClientOptions`, hardcoded default in `http-cloud-client.ts` (**not** exposed in `gateway.config.json`) | `DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS` | `HttpCloudClient` | Per-request abort timeout (10s) |
| `refreshMarginMs` | `CloudAuthOptions`, hardcoded default in `cloud-auth.ts` (**not** exposed) | `DEFAULT_CLOUD_AUTH_OPTIONS` | `CloudAuthTokenCache` | How early (30s before expiry) to treat a cached token as needing refresh |
| `baseDelayMs`, `maxDelayMs` | `BackoffOptions`, hardcoded default in `backoff.ts` (**not** exposed) | `DEFAULT_BACKOFF_OPTIONS` | `computeBackoffDelayMs` | Retry backoff curve (1s → capped at 5 min, doubling, no jitter) |

**Note:** everything in the last four rows is a compiled-in constant today, not something an operator can change per-deployment without editing code — **CONFIRMED BY CODE** (these constructors accept an options object but `Gateway`'s default wiring never passes anything other than the module's own `DEFAULT_*` constant).

---

## 8. FAILURE / OFFLINE ARCHITECTURE

```text
Device unavailable
   ↓
Device.connect()/.read() rejects
   ↓
ReceptionCaptureWorkflow.readAndAssemble() rejects, unchanged, to its caller
   ↓
No key generated, nothing written to SQLite — but ALSO: nothing in the live
process currently calls this path at all (see §1.2), so this failure mode
is proven correct by tests, not yet observable in production.
```

```text
Cloud unavailable
   ↓
fetch() throws → HttpCloudClient returns {outcome:"retryable-error"}
   ↓
SyncEngine: markOutboxRetry → attemptCount++, exponential backoff (1s..5min, deterministic)
   ↓
Row stays PENDING, safely, indefinitely, retried every eligible tick
   ↓
After 10 total attempts → markOutboxFailed (terminal; ops must intervene; row is
   never deleted, no data is silently discarded)
```

```text
SQLite failure
   ↓
init()-time failure (bad path/corrupt file): connection closed, error re-thrown,
   propagates all the way to main.ts's uncaught-rejection handler → process exits 1
   (WinSW then restarts it, see below)
   ↓
mid-operation write failure (rare, single-process/single-connection): the
   surrounding withTransaction() ROLLBACKs the whole attempt so no partial
   state is ever left — CONFIRMED BY CODE, this is explicitly the "no partial
   state" guarantee validated by a dedicated forced-failure test
```

```text
Gateway crashes
   ↓
Node process exits (any reason)
   ↓
WinSW detects the child died
   ↓
<onfailure action="restart" delay="5 sec"/> — WinSW restarts it
   ↓
New process: storage.init() → recoverStaleProcessing(now, 0) sweeps every
   PROCESSING row (necessarily orphaned by the crash) back to PENDING
   ↓
Nothing is lost; anything mid-flight is retried on the next tick, and cloud-side
   idempotency (localIdempotencyKey) plus classifyHttpResponse's "duplicate"
   outcome protect against the row that actually DID reach the cloud right
   before the crash from becoming a second cloud-side row.
```

---

## 9. THE "ONE SENTENCE" EXPLANATION

The gateway is a small, dependency-free Node.js/TypeScript process, deployed as a Windows service, that is meant to sit between a chilling centre's physical weighing scale/milk analyser and the CC-MC cloud API; today it fully and correctly implements the durable local-storage-and-cloud-sync half of that job (SQLite outbox, retry/backoff, idempotent HTTPS delivery to `POST /reception`) while the device-reading half (`ReceptionCaptureWorkflow` plus two in-process device simulators) is built and unit-tested but not yet wired into the process's own automatic startup path, so as it runs today it is best described as **a proven, unattended offline-first sync agent waiting for its capture trigger to be connected.**

## 10. THE "EXPLAIN IT TO MY CEO" VERSION

The gateway is a small program meant to run quietly on a PC at each chilling centre. Windows starts it automatically in the background and restarts it if it ever crashes — nobody has to remember to launch it. Its job is to take a milk reception (weight, fat, SNF, temperature), save it safely on that PC first, and then send it up to our cloud system over the internet — and if the internet drops, or our cloud API is briefly down, nothing is lost: it just keeps the record locally and keeps quietly retrying until it gets through. Today, the "send it to the cloud reliably, even offline" half of that story is fully built and tested. The "actually read it off the physical scale and analyser" half is built and tested against stand-in simulated devices, but isn't yet switched on inside the running program — that's the next piece of wiring left to do before this reads real instruments instead of test doubles.

---

## 11. THINGS I STILL NEED TO LEARN (genuine gaps)

```text
1. Real device protocol details (baud rate, framing, Bluetooth profile) for the
   actual weighing scale and milk analyser hardware — UNKNOWN FROM CURRENT CODE,
   and explicitly, deliberately not guessed at anywhere in this codebase.
2. What will actually trigger a capture in production (a button? a poll loop? an
   operator app?) — UNKNOWN FROM CURRENT CODE; ReceptionCaptureWorkflow exists
   and works but has no caller yet.
3. The cloud API's exact contract beyond what's inferable from this client code
   (apps/api is a separate part of the monorepo not read for this report).
4. The real Windows packaging artifacts (CCMCGateway.exe, node.exe) — this
   sandbox only produced labeled placeholders; genuine install/start/reboot/
   crash-restart behavior has never been exercised on a real Windows machine.
5. Least-privilege service account and file-ACL hardening — explicitly deferred
   to "Phase 12" in the code's own comments, not decided yet.
6. Any plan for HealthService.cloudConnectivity / deviceConnectivity, which are
   permanently stuck at "UNKNOWN" today — the setters exist but nothing calls
   them anywhere in src/.
7. What (if anything) will ever requeue a FAILED outbox row — no such tool
   exists yet, by the code's own admission.
```

---

## 12. FINAL ARCHITECTURE CHEAT SHEET

```text
CC-MC GATEWAY
PURPOSE          → Bridge one chilling centre's local devices to the cloud API;
                    offline-first, unattended, Windows-hosted.
ENTRYPOINT       → src/main.ts → Gateway (src/gateway.ts)
INPUTS           → (intended) physical scale + analyser readings.
                    (actual, today) in-process simulators, driven only by tests.
PROCESSING       → ReceptionCaptureWorkflow validates + assembles + keys a reception
                    (built, tested, NOT auto-wired) → LocalStorage persists it atomically
OUTPUTS          → HTTPS POST to the cloud API's /reception, authenticated via /auth/login
DATABASE         → One file: gateway.sqlite (node:sqlite, WAL). Tables: gateway_metadata,
                    local_transactions, outbox_records, schema_migrations.
EXTERNAL SYSTEMS → Cloud API (HTTPS), Windows SCM/WinSW (process supervision).
                    NOTHING ELSE — no serial, no Bluetooth, no MQTT, no message queue.
FAILURE HANDLING → Outbox state machine + deterministic backoff + crash-safe
                    PROCESSING-row recovery at every startup and every tick.
WINDOWS SERVICE  → WinSW: auto-start, auto-restart-on-crash, raw stdout/stderr log
                    capture. Knows nothing about SQLite/outbox/cloud — pure process
                    supervision, layered cleanly below the application code.
```

```text
IMPORTANT FILES
main.ts                          → process bootstrap, signals, diagnostic mode
gateway.ts                       → subsystem start/stop orchestration
config/config.loader.ts          → fail-fast JSON config validation
device/device-manager.ts         → device registry (0 devices registered today)
capture/reception-capture-workflow.ts → device readings → validated, keyed transaction (untriggered)
storage/sqlite-local-storage.ts  → all SQL, migrations, atomicity, outbox transitions
sync/sync-engine.ts              → offline-tolerant delivery loop (no HTTP code)
sync/http-cloud-client.ts        → the only file that speaks real HTTP
sync/cloud-auth.ts               → JWT cache/refresh
sync/http-response-classification.ts → pure HTTP-status → outcome rules
health/health.service.ts         → in-memory --status snapshot
service/CCMCGateway.xml          → WinSW hosting config (deployment, not app code)
```

```text
MOST IMPORTANT DATA FLOW (the one that actually runs today)
Outbox row already sitting in SQLite (status=PENDING)
 ↓
SqliteSyncEngine's 15s timer claims it
 ↓
HttpCloudClient logs in (cached JWT) and POSTs it to the cloud's /reception
 ↓
classifyHttpResponse turns the HTTP response into created/duplicate/retryable/terminal
 ↓
SqliteLocalStorage marks the row SYNCED (with the cloud's id) — or reschedules
  it with backoff — or marks it FAILED after 10 attempts
 ↓
Nothing is ever silently lost; every row's fate is durable and observable
```
