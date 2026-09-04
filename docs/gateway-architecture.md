# Gateway Architecture: Local Persistence & Offline Sync

**Status:** Living document, started at Checkpoint 3 (`docs/gateway-decision.md`
promised this file "once the skeleton exists," which Checkpoint 2 built),
extended at Checkpoint 4 (§9-§11: device abstraction, simulators, the
normalized reading model, and the capture workflow), extended again
at Checkpoint 5 (§13: real cloud sync - HTTP client, unattended
authentication, cloud-side idempotency, and the full test matrix proving
it), and extended again at Checkpoint 6E (§15: architecture reconciliation
of "LOCAL" against a proposed CLOUD↔GATEWAY↔LOCAL diagram, the CEO-confirmed
manual capture trigger, and the generic RS232 serial transport for the
ESSAE equipment's confirmed baud/data-bits/parity/stop-bits/flow-control
settings - protocol/message parsing remains unimplemented, see §15c;
"Known limitations" was §14 before Checkpoint 6E made room for §15, and is
now §16). Scope: the gateway's internal architecture - module boundaries, the
SQLite schema, the local persistence/offline-sync design, the
device-abstraction and capture-flow design, and (as of Checkpoint 5) the
real gateway-to-cloud integration. Windows service hosting (WinSW,
install/uninstall, crash recovery at the OS level) is
`docs/gateway-decision.md`'s scope, not this file's.

This document is written from the actual, executed implementation, not
speculatively ahead of it - every claim here was verified by a real test
or a real run of the gateway (see each checkpoint's report for commands
and output).

## 1. Module architecture (Checkpoint 2 recap)

```
Device -> Transport -> Parser -> NormalizedReading -> LocalStorage -> SyncEngine -> CloudAPI
```

- `device/`, `transport/`, `parser/`, `normalization/` - protocol-agnostic
  interfaces. Checkpoint 4 adds the first concrete `Device` implementations
  (`device/simulators/*`) - simulator-backed, no real hardware yet (see
  §9). `transport/`/`parser/` remain unimplemented - they are the seam for
  a future byte-level protocol, which simulators don't need (§9).
- `capture/` (new, Checkpoint 4) - the application-level workflow that
  turns device readings into a validated, durable, idempotent local
  transaction. See §11.
- `storage/` - this checkpoint's main subject. Real implementation:
  `SqliteLocalStorage`.
- `sync/` - `SqliteSyncEngine` (Checkpoint 3) plus, as of Checkpoint 5, a
  real `CloudClient` implementation (`HttpCloudClient`) wired into
  `Gateway` by default. See §13.

## 2. Why SQLite, and why `node:sqlite` specifically

SQLite is the obvious choice for a single-process, single-machine, no-DBA
edge device: zero administration, a single file to back up, battle-tested
crash-recovery semantics via its journal/WAL modes (Rule 11: prefer
boring, reliable technology).

The specific driver is Node's built-in `node:sqlite` (`DatabaseSync`)
rather than an external package such as `better-sqlite3`. This matters
architecturally because `better-sqlite3` requires a compiled native
addon - its prebuilt binary must match the exact platform/arch/Node-ABI of
whatever Node build gets vendored into the Windows deployment
(`docs/gateway-decision.md`'s vendored-runtime approach). `node:sqlite`
ships inside Node itself: nothing to prebuild, nothing to match, and
`apps/gateway/package.json`'s `"dependencies"` stays `{}` through this
checkpoint.

The trade-off, stated plainly: `node:sqlite` is still flagged experimental
by Node (its API could change between Node versions). This is an
acceptable risk specifically *because* of the vendored-runtime decision -
the deployed gateway always runs against one exact, pinned `node.exe`
build, so there is no "the user's Node upgraded under us" failure mode.
All `node:sqlite` usage is isolated to one file
(`src/storage/sqlite-local-storage.ts`) behind the `LocalStorage`
interface, so swapping drivers later is a contained change.

**Durability settings:** `PRAGMA journal_mode = WAL` + `PRAGMA synchronous
= FULL` + `PRAGMA foreign_keys = ON`, set on every connection open (SQLite
does not persist `PRAGMA` settings in the file). WAL makes crash recovery
well-defined (a WAL frame is either fully written and replayed, or ignored
- never torn), and `synchronous = FULL` fsyncs on every commit rather than
trusting the OS write cache. This is the safest standard combination,
chosen because the checkpoint's primary directive was explicit: **no data
loss**. The latency cost is irrelevant at this system's actual transaction
volume (a handful of receptions per hour, per centre).

## 3. Schema: what exists, and what was deliberately NOT built

The checkpoint asked for schema in four areas. Each was actually decided
on its own merits, not created by default:

### 3a. Gateway configuration/state

**Decision: configuration stays in the JSON file** (`config/gateway.config.json`,
Checkpoint 2), not SQLite. It is small, human-edited per install, and
already has fail-fast validation (`config.loader.ts`). Duplicating it into
a database table would just be a second source of truth to keep in sync
for no benefit.

**What SQLite *does* hold for gateway state: `gateway_metadata`, a
single-row identity pin:**

```sql
CREATE TABLE gateway_metadata (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  gateway_id TEXT NOT NULL,
  centre_id INTEGER NOT NULL,
  created_at TEXT NOT NULL
);
```

This is not schema for its own sake - it closes a real data-integrity gap.
On first `init()`, the gateway's `gatewayId`/`centreId` (from config) are
pinned into this row. On every subsequent `init()`, the loaded config is
compared against the pinned values; a mismatch throws
`GatewayIdentityMismatchError` and refuses to start. Concrete failure mode
this prevents: an operator copies a `gateway.sqlite` file between two
different centre installs (disk clone, a "restore from backup" onto the
wrong machine). Without this check, the gateway would silently start
attributing another centre's outbox rows to this centre's cloud
credentials once Checkpoint 5 wires real HTTP - a data-integrity incident,
not just a bug.

### 3b. Device configuration/state

**Decision: not built this checkpoint, deliberately.** `DeviceManager`
(Checkpoint 2) already manages device lifecycle in memory; nothing this
checkpoint or the next reads back a persisted device row, and Checkpoint
4 (simulators) hasn't yet defined what a device's persisted config would
even contain (which simulator devices auto-register? What identifies a
simulated scale across restarts?). Building a table now risks guessing at
a shape and having to migrate it away once Checkpoint 4's real
requirements are known - the opposite of the "design the schema around
the actual gateway workflow" instruction. This is a deliberate deferral,
not an oversight - if Checkpoint 4 turns up a concrete persistence need,
it becomes migration 2.

### 3c. Normalized readings / local transactions

**Decision: `local_transactions`, at the *assembled reception* level, not
raw device readings.**

```sql
CREATE TABLE local_transactions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  local_idempotency_key TEXT NOT NULL UNIQUE,
  centre_id INTEGER NOT NULL,
  source_id INTEGER NOT NULL,
  vehicle_id INTEGER NOT NULL,
  quantity_kg REAL NOT NULL,
  fat REAL NOT NULL,
  snf REAL NOT NULL,
  temperature REAL NOT NULL,
  captured_at TEXT NOT NULL,
  created_at TEXT NOT NULL,
  cloud_transaction_id INTEGER NULL
);
```

This shape deliberately mirrors the cloud's `CreateReceptionDto`
(`apps/api/src/reception/dto/create-reception.dto.ts`) - it is the local
mirror of what becomes a `MilkReceptionTransaction` row once synced
(Checkpoint 5), not an invented shape. Raw `ScaleReading`/`AnalyserReading`
values (`normalization/normalized-reading.types.ts`) are NOT persisted as
their own rows: nothing reads them back at this granularity, and the
BRD's actual transactional/sync unit is the completed reception. If
Checkpoint 4's capture flow turns up a real need to persist raw readings
(e.g. for audit trail before assembly), that is a concrete, then-known
requirement to design against - not a guess today.

`cloud_transaction_id` doubles as the "has this synced" signal (`NULL` =
not yet synced). This is deliberate: it means there is exactly ONE place
that says whether a transaction is synced, not two tables that could
disagree. `outbox_records.status` tracks sync *attempt* state (a
different concept - see below), not "is this synced."

### 3d. Outbox/sync records

```sql
CREATE TABLE outbox_records (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  local_transaction_id INTEGER NOT NULL UNIQUE REFERENCES local_transactions(id),
  local_idempotency_key TEXT NOT NULL UNIQUE,
  status TEXT NOT NULL CHECK (status IN ('PENDING','PROCESSING','SYNCED','FAILED')),
  attempt_count INTEGER NOT NULL DEFAULT 0,
  last_attempt_at TEXT NULL,
  next_attempt_at TEXT NOT NULL,
  last_error TEXT NULL,
  claimed_at TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX idx_outbox_eligibility ON outbox_records (status, next_attempt_at);
```

One outbox row per local transaction (1:1, enforced by the `UNIQUE` on
`local_transaction_id`). `local_idempotency_key` is intentionally
denormalized (copied) from `local_transactions` rather than requiring a
join on the sync engine's hot path - safe because both rows are written
in the same atomic transaction and neither is ever updated after
creation. The `(status, next_attempt_at)` index is the query the sync
engine's every tick depends on.

### 3e. Migrations

A real, ordered, tracked migration framework
(`src/storage/schema.ts`) - not a single ad-hoc "CREATE TABLE IF NOT
EXISTS" blob re-run on every start. `schema_migrations` records what has
been applied; each migration runs inside its own transaction so a failed
migration never leaves the schema half-applied. Migration 1
(`initial_schema`) creates all four tables above. Future schema changes
(e.g. a device-state table, once Checkpoint 4 justifies one) become
migration 2, 3, etc. - an existing deployed database upgrades forward
automatically, without a human hand-editing a file on a machine at a
chilling centre.

## 4. Transaction atomicity

**Requirement:** a local transaction and its outbox record must be
created atomically - both exist, or neither does.

**Mechanism:** `SqliteLocalStorage.createLocalTransaction()` wraps both
inserts in one SQLite transaction, using `BEGIN IMMEDIATE` (not plain
`BEGIN`) to acquire the write lock before either `INSERT` runs. Any error
anywhere inside triggers `ROLLBACK` before the error propagates. This is
not a convention the application layer has to remember to honor
correctly on every call site - it is the only code path that creates a
`local_transactions` row at all.

```
BEGIN IMMEDIATE
  SELECT ... WHERE local_idempotency_key = ?         -- idempotent-replay check
  [if found: return existing pair, COMMIT (no-op write)]
  INSERT INTO local_transactions (...)
  INSERT INTO outbox_records (...)
COMMIT
-- any exception anywhere above -> ROLLBACK, re-thrown
```

**A note on a design mistake caught and fixed during this checkpoint:** an
earlier version of this method wrapped the two `INSERT`s in a `try/catch`
that treated any `UNIQUE` constraint violation as "another writer raced
us - recover by re-reading the row." This was wrong, and proven wrong by
actually constructing the failure it claimed to handle (see
`local-transactions.spec.ts`'s TEST 4): because `BEGIN IMMEDIATE` holds
this connection's write lock for the whole transaction, and this gateway
holds exactly one connection, there is no window in which another writer
could commit a conflicting row between the initial `SELECT` and the
`INSERT`s - the scenario the catch block defended against cannot occur.
When a real conflict was forced deliberately (a decoy row pre-inserted to
collide on `outbox_records.local_idempotency_key`), the catch block read
back *this call's own uncommitted insert* as if it were a pre-existing
committed row and crashed looking for a matching outbox record that
didn't exist. The fix: let any insert failure propagate uncaught, so
`ROLLBACK` undoes the whole attempt. Simpler, and correct.

**Verified by:** `local-transactions.spec.ts` TEST 1, TEST 4 (forces a
genuine SQLite constraint violation mid-transaction via a real decoy row,
not a mock) and `subprocess-kill.spec.ts` (a real child process is
SIGKILLed after both `INSERT`s run but before `COMMIT` - see §6).

## 5. Idempotency

**Requirement:** a stable key per logical transaction, generated once, DB-level uniqueness, no new key on retry.

- `generateLocalIdempotencyKey(gatewayId)` (`src/storage/idempotency.ts`)
  uses `crypto.randomUUID()` (Node's built-in CSPRNG-backed UUID v4 - no
  extra dependency), prefixed with the gateway ID for traceability during
  troubleshooting. Called **once**, by the caller, at the point a
  complete reception is first assembled (Checkpoint 4's job) - never by
  `LocalStorage` itself, and never again for a retry of the same logical
  transaction.
- **Enforced at the database level**, not only in application code: `local_idempotency_key TEXT NOT NULL UNIQUE`
  on both `local_transactions` and `outbox_records`. Proven directly by a
  test that bypasses `createLocalTransaction()` entirely and attempts a
  raw duplicate `INSERT` against the table (TEST 3b in
  `local-transactions.spec.ts`) - the constraint, not application logic,
  is what actually rejects it.
- **A retry is idempotent, not merely rejected - but only when the payload
  actually matches (added Checkpoint 4).** If a caller calls
  `createLocalTransaction()` again with a key that already exists, two
  different things can happen, and conflating them would be a real
  correctness bug:
  - **same key + the SAME payload** (every business field, including
    `capturedAt`, identical) - a genuine retry (the caller wasn't sure
    whether its first attempt succeeded, e.g. before a hypothetical
    earlier crash). The existing pair is returned unchanged
    (`wasNewlyCreated: false`), nothing new is written (TEST 3).
  - **same key + a DIFFERENT payload** - `IdempotencyKeyConflictError`
    (`src/storage/errors.ts`) is thrown, naming every conflicting field,
    and nothing is written or changed. **This is the important case to
    get right**: an idempotency key exists to make retries of the *same*
    transaction safe, not to let a caller silently discard new data by
    reusing an old key. If this were instead treated like a normal
    duplicate (silently returning the original row), a caller bug -
    reusing a key across two genuinely different captures - would look
    like the second capture succeeded, when it was actually thrown away
    with no record anywhere that it happened. That is a data-integrity
    failure mode a milk-collection system cannot afford: it would mean a
    real delivery's weight/fat/SNF/temperature values silently vanish in
    favor of an unrelated earlier reading. Comparing `capturedAt` too
    (not just the numeric fields) matters for the same reason: two
    genuinely different capture attempts that happen to produce identical
    weight/fat/SNF/temperature values (plausible - milk quality doesn't
    vary wildly minute to minute) would otherwise be missed as "the same
    payload" just because the numbers matched by coincidence.
  - Verified in `test/storage/idempotency-conflict.spec.ts`: same-payload
    retry returns cleanly; a conflicting `quantityKg` is rejected and
    names the field; multiple conflicting fields are all reported, not
    just the first found; a `capturedAt`-only conflict (every numeric
    field identical) is still caught; and a genuinely identical payload
    is confirmed to NOT false-positive.
- The eventual cloud-side half of idempotency (Checkpoint 5: the cloud API
  honoring `localIdempotencyKey` server-side, returning the existing
  record for a repeat submission) is modeled now, ahead of that
  implementation, via `CloudSendResult`'s `"duplicate"` outcome (§7) - so
  the sync engine already knows what to do with it.

## 6. Outbox state machine

```
                 ┌─────────────────────────────────────┐
                 │                                       │
                 ▼                                       │
   [create] → PENDING ──(claim)──► PROCESSING ──(success)──► SYNCED  [terminal]
                 ▲                     │
                 │                (failure, attempts remain)
                 └─────────────────────┘
                                        │
                                (failure, attempts exhausted,
                                 or a terminal cloud response)
                                        ▼
                                     FAILED  [terminal]
```

- **PENDING** - eligible for a sync attempt once `now >= nextAttemptAt`.
  Initial state, and the state a retried item returns to.
- **PROCESSING** - claimed by a sync engine tick, delivery in flight.
  **Never a terminal state** - see recovery below. Claiming is an atomic,
  conditional `UPDATE ... WHERE id = ? AND status = 'PENDING'`, checked by
  row-count, so a double-claim is a structural non-issue even though this
  gateway is single-process today (defends a future change to that
  assumption without silently reintroducing a race).
- **SYNCED** - the cloud confirmed receipt (`"created"` or `"duplicate"` -
  both are success from the sync engine's point of view: either way, the
  cloud now has exactly one transaction for this key). Terminal, success.
  Also stamps `local_transactions.cloud_transaction_id`, in the same
  database transaction as the status flip.
- **FAILED** - exceeded the configured max attempts, or the cloud
  reported a terminal (non-retryable) error. Terminal, requires
  manual/ops review - **rows are never deleted**, so nothing is silently
  lost (the explicit "gateway is an edge system, SQLite is the source of
  truth" requirement). Recovering a `FAILED` row back to `PENDING` is a
  manual/ops action, not built this checkpoint (see §14, Known
  limitations).

### Avoiding a permanent PROCESSING deadlock

Two independent recovery mechanisms, for two different failure windows:

1. **Startup sweep** (`SqliteLocalStorage.init()`, `staleThresholdMs=0`):
   any row still `PROCESSING` when a gateway process starts is, by
   definition, orphaned - a fresh process has made no claims yet, so it
   cannot legitimately have anything in flight. Every such row is
   unconditionally requeued to `PENDING` before the gateway reports
   itself started. This is the direct fix for "the process dies while an
   item is PROCESSING, the next gateway startup must be able to recover
   it" (TEST 2).
2. **In-process staleness sweep** (`SqliteSyncEngine.tick()`, real
   threshold - default 5 minutes): catches a claim that hangs *within* a
   single long-running process (e.g. a delivery attempt that never
   resolves) without prematurely reclaiming genuinely-in-flight work from
   the current tick.

Both call the same `recoverStaleProcessing(nowIso, staleThresholdMs)`
method with different thresholds - one function, two call sites, each
appropriate to what a fresh process vs. a long-running one can safely
assume.

## 7. Retry / backoff

Deterministic exponential backoff, a pure function of `attemptCount` (not
wall-clock, not random) - `src/storage/backoff.ts`:

```
delay(attemptCount) = min(baseDelayMs * 2^(attemptCount - 1), maxDelayMs)
```

Defaults: 1 second base, 5 minute cap. No jitter: jitter exists to avoid a
thundering herd of many independent clients retrying the same endpoint at
the same instant - at this system's actual scale (one gateway, one
outbox, a handful of transactions per hour per centre), that problem does
not exist, and adding jitter would only make tests harder to assert on
for no real benefit (Rule 11: don't over-engineer).

`SqliteSyncEngine`'s per-item flow on failure
(`SyncEngineOptions.maxAttempts`, default 10): a retryable error
increments `attemptCount`, computes the next backoff delay, and returns
the item to `PENDING` with `nextAttemptAt` set accordingly - `FAILED`
only once attempts are exhausted or the cloud signals a terminal error.
An item whose `nextAttemptAt` is still in the future is invisible to
`findEligibleOutboxItems()`, which is the actual mechanism that prevents
hammering the cloud during an outage - proven directly in
`sync-engine.spec.ts` (a tick immediately following a failure makes zero
additional cloud calls).

## 8. Restart recovery - what each of the five required tests proves

| Test | What it proves | Where |
|---|---|---|
| TEST 1 | Create -> both rows exist | `local-transactions.spec.ts` |
| TEST 2 | PROCESSING + crash -> PENDING again after restart | `outbox-state-machine.spec.ts` (mocked) + `subprocess-kill.spec.ts` (real SIGKILL) |
| TEST 3 | Duplicate key -> no duplicate row, DB constraint enforces it | `local-transactions.spec.ts` (TEST 3 + TEST 3b) |
| TEST 4 | Forced failure mid-transaction -> full rollback, no partial state | `local-transactions.spec.ts` (real constraint violation) + `subprocess-kill.spec.ts` (real SIGKILL before COMMIT) |
| TEST 5 | Multiple PENDING transactions all survive a restart | `local-transactions.spec.ts` |

TEST 2 and TEST 4 additionally have a **real subprocess-kill** version
(`test/restart-recovery/subprocess-kill.spec.ts`): a genuine child Node
process is spawned, reaches a precise, signaled point in a real
transaction (synchronized via a stdout line, not a timing guess), and is
sent a real `SIGKILL` - actual OS-level process death, not a simulated
one. The parent then reopens the same database file with a real
`SqliteLocalStorage` instance, exactly as the gateway does on restart,
and asserts on what genuinely persisted to disk. A further, ad hoc
end-to-end run (documented in the Checkpoint 3 report, not part of the
Jest suite) did the same thing one level up - killing the actual compiled
`dist/main.js` gateway process itself while an outbox item was
`PROCESSING`, then restarting it via `--status` and observing the real
log line `"Recovered outbox rows stuck PROCESSING from a prior run"` and
a correctly non-zero `pendingSyncCount`.

## 9. Device abstraction + simulators (Checkpoint 4)

**Requirement:** prove the complete gateway flow without real hardware,
with an abstraction clean enough that a future real adapter is a drop-in.

- `Device<TReading>` (`src/device/device.types.ts`, defined Checkpoint 2)
  is unchanged in shape this checkpoint - only its header comment grew, to
  document a decision Checkpoint 4 makes concrete: **simulators implement
  `Device` directly, not through `Transport`/`Parser`.** `Transport`
  (`transport/transport.types.ts`) and `Parser` (`parser/parser.types.ts`)
  exist specifically as the seam for a future *byte-level* protocol
  (RS232, Bluetooth) - something that receives a raw byte stream and has
  to frame/decode it into a reading. A simulator has no bytes; it produces
  an already-structured `ScaleReading`/`AnalyserReading` programmatically.
  Routing that through a fake `SimulatorTransport`/`SimulatorParser` would
  mean inventing an arbitrary byte encoding for data that is never
  actually transmitted as bytes - ceremony with no real seam behind it,
  and exactly the kind of invented protocol detail Rule 2/Rule 3 forbid.
  The first implementation to actually need `Transport`/`Parser` will be a
  real hardware adapter (blocked on hardware specs), and it will implement
  the same `Device` interface `WeighingScaleSimulator`/
  `MilkAnalyserSimulator` do now - so the capture layer above `Device`
  never has to know or care whether a given device is simulated or real.
- `SimulatedDeviceBase<TReading>` (`src/device/simulators/simulated-device-base.ts`)
  is the shared implementation both concrete simulators extend: a real
  connection state machine (`DISCONNECTED -> CONNECTED`, `read()` rejects
  outside `CONNECTED`) plus a scriptable outcome queue/default reading -
  not a single hardcoded return value, per the explicit "should behave
  like actual devices" instruction. It deliberately does **not** validate
  the readings it's told to return (an "invalid reading" scenario is just
  a normal reading with a semantically bad value, e.g. negative weight);
  validation is the capture layer's job (§11), keeping "can this device
  produce a value" and "is this value acceptable" as two separate
  concerns.
- `WeighingScaleSimulator` (`kind: "SCALE"`) and `MilkAnalyserSimulator`
  (`kind: "ANALYSER"`) are thin subclasses adding only a device-specific,
  explicitly-optional `simulateRealisticReading(seed, capturedAt)` demo
  helper (§10) - all core scripted behavior comes from the shared base.
- Every minimum scenario the checkpoint calls for is covered by the base
  class's API, exercised in `test/device/weighing-scale-simulator.spec.ts`
  and `milk-analyser-simulator.spec.ts` (12 and 11 tests respectively):
  normal reading (`setDefaultReading`/`setNextReadings`), repeated reading
  (the default repeats indefinitely once the queue drains), invalid
  reading (a queued semantically-bad value, returned unvalidated),
  device unavailable (`simulateConnectFailure()` - next `connect()` fails,
  one-shot; `goOffline()` - forces a *currently connected* device into
  `ERROR` immediately, modeling a mid-session drop distinctly from never
  having connected), and delayed response (`simulateNextReadDelay()`,
  one-shot, proven with real elapsed-time assertions, not mocked timers).

## 10. Normalized reading model

`ScaleReading`/`AnalyserReading` (`src/normalization/normalized-reading.types.ts`)
were already defined in Checkpoint 2 and are **unchanged** this checkpoint
- confirming, not just assuming, that the Checkpoint 2 design holds up
once a real (simulated) producer exists was itself part of this
checkpoint's work. Each field is labeled BRD-defined or ASSUMED in the
source file itself (e.g. `ScaleReading.stable` is explicitly flagged
ASSUMED - a plausible "settled" flag real scales commonly report, but not
BRD-confirmed or hardware-confirmed). Nothing manufacturer-specific was
added. The seeded PRNG (`src/device/simulators/seeded-random.ts`,
mulberry32) backing `simulateRealisticReading()` is demo-only: never
called by anything under `test/`, and requires an explicit seed argument
(no implicit default), so a caller can never accidentally get
non-deterministic behavior - satisfying "if randomness is useful for
demos, make it explicitly optional and seedable."

## 11. Capture workflow & multi-device assembly

**Requirement:** `Device -> normalized reading -> reception assembly ->
generate localIdempotencyKey() once -> LocalStorage.createLocalTransaction()`,
with the key generated exactly once, never regenerated on a sync retry.

- `assembleReception()` (`src/capture/reception-validation.ts`) validates
  a combined scale + analyser reading, mirroring
  `apps/api/src/reception/dto/create-reception.dto.ts`'s `class-validator`
  rules **exactly** - not stricter, not looser: `centreId`/`sourceId`/
  `vehicleId` must be integers; `quantityKg`/`fat`/`snf` must be numbers
  `>= 0`; `temperature` must be a number with **no minimum** (the DTO has
  no `@Min()` there - milk temperature can legitimately read negative -
  and this gateway does not invent a stricter local rule the cloud
  doesn't enforce). Hand-written rather than a `class-validator` call,
  since the gateway carries no NestJS-style dependency (Rule 11: minimal
  deps for a small edge process) - the DTO is cited by name in the code
  comment as the single source of truth to keep the two in sync. On
  failure it throws `InvalidReadingError` listing **every** violation
  found, not just the first, and returns nothing partial - satisfying
  failure semantic #2 ("no invalid transaction should enter SQLite").
- `ReceptionCaptureWorkflow` (`src/capture/reception-capture-workflow.ts`)
  is the "application-level workflow abstraction" the checkpoint calls
  for when the real device interaction sequence isn't established by the
  BRD or hardware specs. It makes two assumptions, both documented in the
  class's own doc comment (and both intentionally easy to revise in one
  place if a real sequencing requirement later emerges):
  1. **Concurrent reads** (`Promise.all([scale.read(), analyser.read()])`),
     not sequential - the BRD does not specify an operator sequence for
     the two devices, and concurrent reads carry the fewest hidden
     ordering assumptions. Proven directly (not just asserted) in
     `reception-capture-workflow.spec.ts`'s "read CONCURRENTLY" test: two
     40ms-delayed reads complete in well under 80ms.
  2. **`capturedAt` = assembly time** (wall-clock when both reads have
     returned), not either device reading's own `capturedAt` - avoids
     inventing a reconciliation rule for two timestamps the BRD never
     asked the gateway to reconcile. Proven directly with a fixed `now()`
     injected via the workflow's constructor.
  - **Split into `readAndAssemble()` + `persist()`** specifically so the
    idempotency key is generated exactly once. `readAndAssemble()` reads
    both devices, validates, and generates the key; `persist()` writes an
    already-assembled `CreateLocalTransactionInput` to `LocalStorage` and
    can be safely retried on its own (re-calling `persist()` with the same
    input hits `createLocalTransaction()`'s existing idempotent-retry path,
    §5) **without ever calling `readAndAssemble()` again** - the one thing
    the checkpoint explicitly forbids ("a retry must never call this
    function again"). `captureReception()` is a convenience wrapper
    (`persist(await readAndAssemble(context))`) for the common case that
    doesn't need the split.
- All seven numbered failure semantics from the checkpoint are each
  covered by a dedicated test in `reception-capture-workflow.spec.ts`
  (`#1` through `#7` in the test names), plus a transaction->outbox
  integration test, a cloud-unavailable simulation (via the real
  `SqliteSyncEngine` + `FakeCloudClient`), and a restart/recovery
  integration test (capture, close, reopen `SqliteLocalStorage`, confirm
  still `PENDING` and syncable) - see `apps/gateway/README.md`'s Testing
  section for the full test inventory.

## 13. Real cloud sync (Checkpoint 5)

**Requirement:** connect the gateway's SQLite/outbox to the real NestJS
API/PostgreSQL over HTTPS, without breaking any existing web workflow,
RBAC, centre isolation, transaction numbering, or audit behavior, and
with the cloud enforcing idempotency so a gateway retry after any kind of
network failure can never create two cloud transactions.

### 13a. Architecture

```
SqliteSyncEngine -> CloudClient (interface) -> HttpCloudClient -> NestJS API -> PostgreSQL
```

`SqliteSyncEngine` (Checkpoint 3) is completely unchanged - it still only
knows the `CloudClient` interface (`src/sync/cloud-client.types.ts`), not
HTTP, fetch, or JSON. `HttpCloudClient` (`src/sync/http-cloud-client.ts`)
is the first real implementation, using Node's built-in global `fetch`
(stable since Node 18) - no new npm dependency, the same reasoning that
chose `node:sqlite` over `better-sqlite3` in Checkpoint 3. `Gateway`
(`src/gateway.ts`) now constructs a real `HttpCloudClient` +
`SqliteSyncEngine` by default and starts/stops it in `start()`/`stop()`,
replacing the Checkpoint 3/4 deliberate non-wiring (that checkpoint's
`sync.types.ts` doc comment explained exactly why starting a fake-backed
engine then would have meant "silently syncing to nowhere" - Checkpoint 5
is what makes starting it meaningful).

### 13b. Cloud-side idempotency

`apps/api/src/reception/dto/create-reception.dto.ts` gains one additive,
optional field: `localIdempotencyKey`. The existing web client never
sends it and is completely unaffected - `ReceptionService.create()`
branches on its presence, and the non-idempotent path is byte-for-byte
the same logic as before Checkpoint 5 (it now also explicitly persists
`localIdempotencyKey: null`, and the response gains an additive
`outcome: "created"` field - nothing existing had to change to accommodate
this).

The idempotent path (`ReceptionService.createIdempotent()`) does NOT do a
"check, then insert" - that has a race window. It does the insert first,
using TypeORM's query builder to issue:

```sql
INSERT INTO milk_reception_transactions (..., "localIdempotencyKey") VALUES (...)
ON CONFLICT ("localIdempotencyKey") DO NOTHING
RETURNING id
```

against the column's existing nullable unique constraint (from the
original schema - no new migration needed). This is the actual
correctness boundary, not an application-level pre-check: Postgres
genuinely blocks a second concurrent `INSERT` targeting the same key at
the database level until the first transaction commits or rolls back,
then correctly resolves to "conflict, do nothing" or "no conflict,
proceed" - verified empirically (not just asserted) via real concurrent
requests, both a standalone sanity script and the full e2e/cloud-integration
suites, every time producing exactly one row for a raced key.

- **`RETURNING id` present** -> this call actually inserted the row: set
  the real `transactionNumber` (`{centreCode}-{id}`, same authority and
  format as the existing non-idempotent path - see `docs/assumptions.md`
  #transaction-numbering), write the audit record, return
  `{ ...transaction, outcome: "created" }`.
- **`RETURNING id` absent** -> a row with this key already existed. Load
  it and compare the incoming payload's business fields
  (`centreId`/`sourceId`/`vehicleId`/`quantityKg`/`fat`/`snf`/`temperature`
  - deliberately NOT `operatorUserId`, since an auth-retry can legitimately
  be a different login than the first attempt, per §13d) against the
  existing row:
  - **identical** -> genuine retry. Return the existing row unchanged,
    `outcome: "duplicate"`. **No second audit record is written** - this
    is the crux of "a duplicate/idempotent success is not the same as
    creating a new transaction."
  - **different** -> a real payload conflict, not a legitimate retry
    (reusing a key for two different logical transactions is a caller
    bug). Throws `ConflictException` (HTTP 409) naming every conflicting
    field and the existing transaction's id. Nothing is modified, no
    second audit record is written.

Both `created` and `duplicate` are HTTP 201 - deliberately, so the
existing web client's `res.status === 201` assumption never had to
change, and so this checkpoint didn't need to add `express`'s `@Res`
just to vary a status code. `outcome` in the response body is the
discriminator instead. **This detail mattered in practice**: see §13f.

### 13c. Gateway centre security (no parallel auth mechanism)

The gateway authenticates through the exact same `/auth/login` -> JWT ->
`JwtStrategy` -> `PermissionGuard`/`CentreAccessService` pipeline any
human user goes through - not a parallel mechanism. Concretely
(`apps/api/src/seed.ts`):

- A new `GatewayService` role, granted only `RECEPTION_CREATE`
  (least-privilege - a compromised or buggy gateway credential cannot
  read dashboards, audit logs, or anything beyond creating receptions).
- Two seeded service-account `User` rows, one per seeded centre
  (`gateway-blr-cc-01@ccmc.local`, `gateway-mys-cc-01@ccmc.local`), each
  with a single-centre `UserCentreAssignment` - never `allCentres`.

`CentreAccessService.assertCanAccess()` required zero code changes to
enforce this: a Bangalore-scoped gateway sending `centreId` for Mysore
gets the same `ForbiddenException` (403) any human user scoped to the
wrong centre would get. Proven both at the raw HTTP/API layer
(`apps/api/test/reception-idempotency.e2e-spec.ts`) and through the real
gateway `HttpCloudClient` against the real live API
(`test/cloud-integration/centre-security.spec.ts`), in both directions.

### 13d. Gateway authentication strategy (unattended service)

The gateway runs unattended and the cloud's JWT is short-lived (8h
default) - it cannot assume a token lives forever. `CloudAuthTokenCache`
(`src/sync/cloud-auth.ts`) implements: obtain -> cache -> reuse while
valid -> proactively refresh once within `refreshMarginMs` (30s default)
of the token's real `exp` claim (decoded client-side, unverified - only
used for refresh timing, never a trust decision: the cloud API's own
`JwtStrategy` is the only thing that ever verifies a token) -> on a real
401, `HttpCloudClient` invalidates the cache and retries the SAME request
object exactly once with a freshly-obtained token. A second 401 in a row
is terminal, not an infinite loop.

**Critically, an auth retry never regenerates `localIdempotencyKey`** -
the retried request is the literal same in-memory payload object,
constructed once by `SqliteSyncEngine.toPayload()` from the already-durable
`LocalTransaction`. Proven with a real expired token (not a mocked clock)
in `test/cloud-integration/auth-expiry.spec.ts`: a genuinely-expired JWT
triggers a real 401 from the live API, the client re-authenticates, and
the retried request completes the exact same logical transaction -
exactly one PostgreSQL row, exactly one audit event.

Credentials (`cloudAuthEmail`/`cloudAuthPassword`) live in
`config/gateway.config.json` - the same trust model as `apps/api/.env`
(local, gitignored, plaintext, OS-file-permission-protected, not an
application-level secret store). No password, JWT, or `Authorization`
header value is ever logged - every log line in `cloud-auth.ts` and
`http-cloud-client.ts` logs only IDs, booleans, status codes, and
durations (verified directly: `http-cloud-client.spec.ts`'s
"never logs..." test greps the actual captured log output for the
secret values and the literal string `"Bearer "`).

### 13e. Response classification and retry policy

`classifyHttpResponse()` (`src/sync/http-response-classification.ts`) is
a pure function, independently unit-tested with no HTTP involved, mapping
`(status, body)` to exactly one of:

| Classification | HTTP | Meaning | SyncEngine effect |
|---|---|---|---|
| `success` (`created`/`duplicate`) | 201 (both) | Cloud has exactly one transaction for this key | `SYNCED`, stamp `cloudTransactionId` |
| `auth-retryable` | 401 | Token rejected | `HttpCloudClient` re-authenticates and retries once internally - `SyncEngine` never sees this classification, only its outcome |
| `retryable` | 429, 5xx, network error, timeout | Transient | Back to `PENDING` with exponential backoff (§7) |
| `terminal` | 409, other 4xx | Will never succeed unchanged | `FAILED` immediately, no further attempts |

This directly implements the checkpoint's required distinction between a
duplicate/idempotent success (still success) and a payload conflict
(terminal failure) - both are "the cloud responded," but only one means
"try again later," and neither is confused with the other. Network-level
failures (connection refused, dropped, timed out) never reach
`classifyHttpResponse()` at all - there is no HTTP response to classify -
and are caught directly in `HttpCloudClient.attempt()`'s `try/catch`
around `fetchWithTimeout()`, always resulting in `retryable-error`.

All 7 required network-failure scenarios (unreachable, timeout, dropped,
500, 401, 409, successful-retry-after-failure) are proven against real
transports (real TCP servers, and the real live API where the scenario is
about real API/auth/idempotency behavior specifically) in
`test/cloud-integration/network-failure.spec.ts` - see that file's header
comment for exactly which scenarios use which kind of real infrastructure
and why.

### 13f. A real bug this work caught (and the general lesson)

The mandatory concurrent-duplicate cloud-integration test
(`test/cloud-integration/concurrent-duplicate.spec.ts`) initially failed:
two genuinely simultaneous same-key requests both reported outcome
`"created"` instead of one `"created"`/one `"duplicate"`. Checking
Postgres directly first (not guessing) showed exactly one row existed -
so the *data* was correct; the *gateway's own report of what happened*
was wrong. Root cause: `classifyHttpResponse()` was inferring `created`
vs. `duplicate` from the HTTP status code alone (`201 -> created`), but
§13b's design deliberately returns HTTP 201 for both outcomes, varying
only the response body's `outcome` field. Fixed by reading `outcome` from
the body first, falling back to the old status-code heuristic only when
the body doesn't carry one (defensive, not load-bearing). Three
regression tests were added
(`test/sync/http-response-classification.spec.ts`) reproducing exactly
this HTTP-201-with-both-outcomes shape.

A second, smaller bug surfaced only once real (not mocked) network
failures were exercised: `describeNetworkError()`'s `err instanceof
Error` check is false for a genuine `AbortError` when Node's built-in
fetch throws it under Jest's "node" test environment specifically (a
cross-realm/vm-context mismatch between the error Node's fetch
constructs and the `Error` global test code sees) - so a real, correctly-
firing timeout was reported as `"Unknown network error"` instead of
`"Request timed out"`. Fixed by duck-typing (`.name`/`.message`) instead
of `instanceof Error`; regression tests added at both the unit level
(`http-cloud-client.spec.ts`) and left provable at the integration level
(`network-failure.spec.ts`'s real-timeout scenario). Neither bug affected
correctness of what got stored (Postgres's own constraint was always the
real safety net) - both were about the gateway accurately *reporting*
what happened, which matters just as much for an unattended system's
logs/observability and any future ops tooling that trusts `CloudSendResult`.

### 13g. Test environment

Two genuinely different kinds of test exist, and it matters which is
which:

- **Unit tests** (`test/sync/*.spec.ts`, part of the default `pnpm test`
  run) - `HttpCloudClient`/`CloudAuthTokenCache`/`classifyHttpResponse()`
  against a scripted fake `fetch` or injected fakes. Fast, hermetic, no
  real network or database. This is where exact retry counts, logging
  content, and precise timing (a real `AbortController` firing) are
  asserted.
- **Cloud-integration tests** (`test/cloud-integration/*.spec.ts`, a
  SEPARATE Jest project, `pnpm test:cloud`) - a real dedicated Postgres
  database (`ccmc_gateway_test`), the real compiled NestJS API spawned as
  a real child process, and real gateway SQLite. Nothing here is mocked.
  This is where the checkpoint's mandatory scenarios (critical lost-
  response, concurrent duplicate, centre security, auth expiry) are
  actually proven end-to-end, not simulated. See
  `apps/gateway/README.md`'s "Cloud-integration suite" section for the
  full file inventory and how to run it.

### 13h. Production vs. local-dev transport

Local development may use plain HTTP (`cloudApiBaseUrl` in
`gateway.config.json` is just a URL - nothing enforces a scheme). **A
production gateway-to-cloud deployment must use HTTPS** - this is a
deployment/configuration requirement (the value operators put in
`cloudApiBaseUrl`), not something `HttpCloudClient` enforces in code,
consistent with how `apps/api` itself does not terminate TLS (that is
infrastructure's job in any real deployment, per that project's own
existing conventions). Nothing in this checkpoint disables TLS
verification, and `fetch` performs normal certificate validation against
an `https://` URL with no gateway-side override.

## 15. Architecture reconciliation, manual capture trigger, and real serial transport (Checkpoint 6E)

### 15a. What "LOCAL" actually means in this system - reconciled against a proposed CLOUD↔GATEWAY↔LOCAL diagram

A sketch was proposed showing `CLOUD ↕ GATEWAY ↕ LOCAL`, with the device
feeding into the Gateway, and asked whether LOCAL and CLOUD both
synchronize *through* the Gateway as two peer systems it bridges. **That
reading does not match this repository.** Verified directly (not
inferred) by reading `apps/gateway/src/storage/*`, `apps/gateway/src/sync/*`,
`apps/api/src/**`, and `apps/web/src/**`:

- **"LOCAL" is not a separate application, server, or database.** It is
  `gateway.sqlite` - one file, opened by `SqliteLocalStorage`
  (`src/storage/sqlite-local-storage.ts`), living inside the same Node.js
  process as everything else in `apps/gateway`. There is no second
  process, no local HTTP server, no local database server (SQLite is an
  embedded, in-process library, not a server) anywhere in this repository
  that "LOCAL" could refer to instead.
- **The real architecture is linear, not a bridge between two peers:**
  ```
  Device -> Gateway (in-process) -> gateway.sqlite (local_transactions + outbox_records)
                                          |
                                   SqliteSyncEngine.tick()
                                          v
                                  HttpCloudClient -> NestJS API -> PostgreSQL
  ```
  SQLite (LOCAL) only ever talks to the Gateway process that embeds it -
  never directly to the cloud, and never directly to a device. The Gateway
  is not "bridging" two independent systems that also talk to each other;
  SQLite is a component *of* the Gateway process, not a peer system next
  to it.
- **Answering the ten questions directly, in order:**
  1. **What does "LOCAL" mean?** The gateway's own embedded SQLite
     database (`gateway.sqlite`), holding `local_transactions` and
     `outbox_records` (§3c/§3d above).
  2. **Is LOCAL the gateway's SQLite database?** Yes - exactly and only
     that.
  3. **Is LOCAL a separate local application/server/database?** No.
     Confirmed by grepping the entire repository: there is no second
     server process, no local REST/IPC endpoint, no local database server
     anywhere `apps/gateway` could be talking to.
  4. **Does the current implementation support LOCAL ↔ GATEWAY
     synchronization?** This question doesn't apply as asked - LOCAL is
     not a separate thing from GATEWAY to synchronize *with*; it's data
     the Gateway process directly reads/writes in-process via
     `LocalStorage`'s method calls (`createLocalTransaction`,
     `getOutboxRecordById`, etc.) - not a network or IPC sync of any kind.
  5. **Does the current implementation support GATEWAY ↔ CLOUD
     synchronization?** Yes - this is real and extensively built:
     `SqliteSyncEngine` reads `outbox_records` from SQLite and pushes them
     to the cloud API via `HttpCloudClient` (§13 above), with real retry,
     backoff, and idempotency.
  6. **Does the current implementation support LOCAL ↔ CLOUD
     synchronization?** Only in the sense that #5 already covers - SQLite
     never talks to the cloud directly; the Gateway process's
     `SqliteSyncEngine` reads SQLite and calls the cloud on SQLite's
     behalf. There is no separate "LOCAL syncs independently" pathway,
     and none should be built - see the STOP condition in §15d below for
     why an independent LOCAL↔CLOUD path was explicitly not invented.
  7. **Is the Gateway intended to be the synchronization authority/
     bridge?** Yes, but specifically as a bridge between physical DEVICES
     and the CLOUD, using its own embedded SQLite as durable local
     storage/outbox along the way - not as a bridge between two
     independent peer systems called "LOCAL" and "CLOUD."
  8. **Does the current architecture match the attached diagram?** Not as
     literally drawn. The diagram's `LOCAL ↕ GATEWAY ↕ CLOUD` shape
     implies LOCAL and CLOUD are comparable peers the Gateway sits
     between and bridges symmetrically. The real shape is asymmetric and
     linear: `Device -> Gateway process (embeds SQLite) -> Cloud`, where
     SQLite is a component of the Gateway, not a peer of the Cloud.
  9. **CURRENT vs. INTENDED architecture, shown separately:**
     ```
     CURRENT (this repository, verified):

       Device -> [ Gateway process
                     ReceptionCaptureWorkflow
                     -> SqliteLocalStorage (gateway.sqlite: local_transactions, outbox_records)
                     -> SqliteSyncEngine -> HttpCloudClient
                   ] -> NestJS API -> PostgreSQL

     INTENDED (per the proposed sketch, as a literal reading):

       Device -> Gateway <-> LOCAL (peer system)
                    ^
                    v
                 CLOUD (peer system)
     ```
     These are genuinely different shapes, not a labeling difference: the
     "INTENDED" reading requires LOCAL to be an addressable system the
     Gateway calls out to and could synchronize independently of the
     cloud - nothing like that exists or was ever built. The "CURRENT"
     shape is what Checkpoints 2-5 actually implemented and is what all
     177 gateway unit tests, 18 cloud-integration tests, and Checkpoint
     6D/6E's real end-to-end runs exercise.
  10. **What would have to change to implement the INTENDED
      architecture?** A new, currently-nonexistent capability: LOCAL would
      need to become an addressable system in its own right (e.g. a local
      HTTP/IPC server the Gateway process exposes, or a genuinely separate
      local application), with its own defined sync contract to the
      Gateway, independent of - or at least distinguishable from - the
      Gateway's existing SQLite-embedded outbox. **This has not been
      built, was not requested by any BRD section found in this
      repository, and was not built speculatively here** - inventing it
      now would be exactly the kind of unrequested architecture rewrite
      the project's rules forbid. If a genuine requirement for LOCAL to be
      an independent, separately-addressable system emerges, it needs its
      own explicit design decision (mirroring how `docs/gateway-decision.md`
      exists as its own document for the Windows-service decision),
      not a guess folded into this document.

**Conclusion: the diagram was a reasonable question to ask, and reconciling
it surfaced a real, useful clarification - but it does not describe this
repository, and nothing was changed to make it match.** The gateway's
actual role (device -> durable local outbox -> cloud, all within one
process) already satisfies "the Gateway is the synchronization authority"
in the sense that matters: it is the one component with authority over
whether the cloud has received a given local transaction (SQLite's own
`local_idempotency_key` UNIQUE constraint + `outbox_records.status`, §4-§6
above), it just doesn't do that by bridging two independent peer systems.

### 15b. Manual capture trigger (CEO-confirmed, Blocker 2)

**Confirmed requirement:** the operator explicitly initiates a capture
(manual trigger), and this is distinct from cloud synchronization, which
remains the outbox/`SyncEngine`'s job, unaffected by how a capture was
triggered.

**What already satisfies this, verified by an actual run this checkpoint
(not just code inspection):** `apps/gateway/scripts/simulate-capture-cli.ts`
(`pnpm simulate`, added the prior checkpoint) is a real, working manual
capture trigger today. An operator runs the command, types field values
(or accepts defaults), and presses Enter once - that single keypress calls
the real `ReceptionCaptureWorkflow.captureReception()`, which reads the
device(s) (currently `WeighingScaleSimulator`/`MilkAnalyserSimulator`,
since no ESSAE parser exists yet - §15c), persists via the real
`SqliteLocalStorage`, and the CLI then explicitly runs one `SyncEngine.tick()`
so the run has an immediate result. **Nothing was bypassed**:
`LocalStorage`/`outbox_records`/`SqliteSyncEngine` are the exact same real
components the long-running gateway service uses (see §13a and
`apps/gateway/scripts/simulate-capture-cli.ts`'s own header comment for
the full "Option A, in-process, shared gateway.sqlite" architecture
explanation). This already IS the "Operator -> Manual Capture -> Scale +
Analyser reading -> Assemble reception -> Local persistence -> Outbox ->
Cloud synchronization" flow the CEO's directive describes, running against
simulator device readings today and requiring no change to become a real
hardware capture trigger once §15c's protocol blocker clears (only the
`Device` implementations passed into `ReceptionCaptureWorkflow` would need
to change, not the trigger, the workflow, or anything downstream of it).

**What was NOT built, and why:** a manual capture *button inside the React
web app* (`apps/web`) that triggers a real Gateway-side device capture.
This is a genuine, structural gap, not an oversight - see §15d's STOP
condition.

### 15c. Real serial transport - CEO-confirmed ESSAE transport parameters

**Confirmed, not guessed:** baud rate 9600 (configurable), 8 data bits, no
parity, 1 stop bit, no flow control, for ESSAE equipment.

**What was implemented** (`src/transport/serial-transport.ts`,
`src/transport/serial-transport.types.ts`):

- `SerialTransport implements Transport` (the exact seam
  `transport.types.ts` already defined, unchanged, specifically for this
  purpose - see §9's explanation of why simulators don't use it but a real
  hardware adapter would). It opens a real OS serial port at a
  configurable `portPath`/`baudRate`, with the other three parameters
  fixed to `ESSAE_CONFIRMED_SERIAL_PARAMETERS` (a named constant, not a
  magic number, documented as "confirmed by the CEO, not guessed, do not
  change without a new explicit confirmation").
- `GatewayConfig.serial` (`config.types.ts`/`config.loader.ts`) - an
  OPTIONAL `{ portPath, baudRate }` block in `gateway.config.json`,
  consistent with this project's existing configuration pattern (§3a:
  "configuration stays in the JSON file... small, human-edited per
  install"). Optional because most current deployments/tests still run
  purely against simulator devices with no real serial hardware wired up.
  When present, both fields are validated fail-fast, exactly like every
  other required field in this loader.
- Uses the `serialport` npm package (no built-in Node.js serial API
  exists, unlike SQLite/HTTP). **This reintroduces a native-addon/
  vendored-runtime-ABI risk this project deliberately avoided for SQLite
  and HTTP** (`node:sqlite` over `better-sqlite3`, built-in `fetch` over a
  library - §2) - flagged explicitly in `serial-transport.ts`'s header
  comment and in `packaging/build-deployment.mjs`'s generated
  `app/node_modules/README.txt`, not silently accepted. Verified to
  install and load in this Linux sandbox (a prebuilt binary exists for
  linux-x64); **NOT verified against the actual vendored Windows
  `node.exe`**, which is itself still a placeholder file as of Checkpoint
  6 (`packaging/build-deployment.mjs`) - a real production packaging step
  still needs to install/rebuild `serialport` targeting that exact
  runtime, which has not happened.
- Tested (`test/transport/serial-transport.spec.ts`, 10 tests) against
  `SerialPortMock` (the real `serialport` package's own mock, backed by
  `@serialport/binding-mock`) - configuration (the confirmed parameters
  genuinely reach the opened port, and baud rate is genuinely
  configurable), connection lifecycle (open/isOpen/close), failure
  handling (opening a nonexistent port rejects; a serial-level 'error'
  event is logged, not left to crash the process unhandled), and
  disconnect/reconnect (including a real bug this work caught and fixed:
  re-opening the same transport instance after a close would otherwise
  stack a second `'data'` listener and deliver each chunk to the
  application twice - fixed by clearing prior listeners before
  re-registering on every `open()`).

**What was deliberately NOT implemented, and why:** the ESSAE message
protocol - no packet format, framing, command bytes, response bytes,
checksum algorithm, terminator, or field-position parsing exists anywhere
in this repository. `parser/parser.types.ts`'s `Parser<TReading>`
interface remains exactly as before (unimplemented) - it is the seam a
real `EssaeScaleParser`/`EssaeAnalyserParser` will implement once a real
protocol specification is supplied. **Per the project's own required
phrasing: transport configuration is known; protocol semantics remain
unverified.** No real hardware read was performed or claimed - the tests
above prove byte-level transport behavior against a virtual port, nothing
about the ESSAE protocol, and nothing about real hardware.

### 15d. STOP condition: browser-based manual capture cannot be built without inventing a new mechanism

The directive's Section D asked for a "Manual Capture" action inside the
React web app that flows through `Scale + Analyser -> Accepted/Hold ->
Local persistence -> Cloud sync -> History/Audit`. Inspecting
`apps/web/src/api/client.ts` (every frontend call is `fetch(\`/api${path}\`)`,
proxied by Vite to the cloud API at `:3000` - `apps/web/vite.config.ts`)
and the entire `apps/gateway` source confirms: **the browser only ever
talks to the cloud API. There is no HTTP endpoint, WebSocket, or any other
mechanism anywhere in this repository for the browser (or the cloud API,
on the browser's behalf) to reach a specific chilling centre's Gateway
process and ask it to perform a device capture.** The Gateway makes only
outbound calls to the cloud (§13a) and exposes nothing inbound - not to
the cloud, and not to a local network listener either.

Building a browser-triggered Gateway capture would require inventing one
of: a local HTTP/WebSocket server on the Gateway machine the browser could
somehow reach (the chilling-centre PC and the operator's browser are not
guaranteed to be on a reachable network path to each other, and nothing in
the BRD/architecture docs specifies one), or a cloud-mediated
"command/queue" channel (cloud API stores a pending command, Gateway polls
for it) - a real, non-trivial architecture decision, not a small frontend
addition. **Per this task's own explicit rule ("If a required backend
capability is missing, report it before implementing a frontend
workaround" / "STOP and report instead of guessing... missing API
contract... conflicting architecture"), this was not built.** No new
endpoint, no new IPC, no new IPC-like polling mechanism was invented.

> **Update (Checkpoint 7, §17):** a local HTTP server on the Gateway
> machine now exists (`src/local-api/local-api-server.ts`), reachable from
> the browser on the same machine/LAN. This does **not** change the
> finding above: that server is **read-only** (status + already-persisted
> local transaction data), with no route that triggers a device capture or
> sends any command to the Gateway. The gap identified here - no inbound
> *command* path from the browser to the Gateway's device pipeline - is
> unchanged. See §17 for what the new local API actually does and does
> not do.

**What this means concretely:** the existing `apps/web/src/pages/ReceptionPage.tsx`
"New reception" form is real and continues to work exactly as before - an
operator types quantity/fat/snf/temperature directly into the browser and
it is submitted straight to the cloud API (`POST /reception`). This is a
**structurally separate, pre-existing capability**: manual DATA ENTRY from
a human reading a physical scale/analyser display and typing the numbers
in, which has never gone through `apps/gateway`, `gateway.sqlite`, or the
outbox at all (confirmed by reading the component's `handleSubmit`). It
must not be confused with, or silently merged into, "manual capture via
the Gateway's own device pipeline" - doing so would either bypass
LocalStorage/Outbox/SyncEngine (forbidden) or require the invented
mechanism above. **This form was not modified this checkpoint.** The real,
working manual capture trigger for the Gateway's own device pipeline
today is §15b's `pnpm simulate` CLI, run by whoever is physically at (or
remoted into) the chilling-centre machine - not a browser action.

### 15e. STOP condition: no settings backend/UI exists for baud rate configuration

Verified by grepping the entire repository (`apps/api`, `apps/web`,
`packages/shared-types`) for "settings" (case-insensitive): **zero
matches.** There is no settings module, controller, DTO, database table,
or frontend page anywhere in this codebase - not a generic one, and not
one specific to the gateway or serial configuration. `quality-rules` is
the closest existing precedent (a real, editable, per-centre backend
configuration surface - `GET/PATCH /quality-rules`), but it is
purpose-built for quality thresholds, not gateway hardware configuration,
and the gateway does not currently read anything from the cloud API at
startup (it only ever calls `/auth/login` and `POST /reception` - §13a) -
there is no existing pull mechanism for a gateway to fetch its own
configuration from the cloud even if a settings endpoint existed.

**What this means:** baud rate (and `portPath`) IS configurable today,
exactly the way every other gateway configuration value already is -
`gateway.config.json`, edited locally per install (§3a's established
pattern, and `GatewayConfig.serial` above). **A web-UI-exposed settings
page for it was not built**, because doing so would mean inventing a new
settings backend (API endpoint + DTO + persistence + a way for the
Gateway to pull it) where none exists - exactly the "do not invent a new
settings subsystem" / "report before creating a new backend contract"
instruction. If a web-configurable baud rate is genuinely required (as
opposed to a locally-edited config file, which already satisfies
"configurable" literally), that is a real, separate design decision
requiring its own settings backend - not something to guess at here.

## 16. Known limitations

- **`FAILED` items have no built-in path back to `PENDING`.** This is
  intentional for this checkpoint (a human should look at *why* something
  permanently failed before blindly retrying it), but there is currently
  no tool to do that requeue - an ops/support capability worth adding now
  that Checkpoint 5's real cloud errors (409 conflicts, other terminal
  4xx) give a concrete sense of what actually ends up `FAILED` in
  practice.
- **`HealthService.cloudConnectivity` starts `UNKNOWN` and now flips to
  `CONNECTED` on a real successful sync, but never flips to
  `DISCONNECTED`** (Checkpoint 5, re-investigated in Checkpoint 6B,
  partially wired in Checkpoint 6D). `HttpCloudClient`/`SqliteSyncEngine`
  make real cloud calls and log their real outcomes; Checkpoint 6B found
  this was not a simple wiring job because `CloudSendResult`'s
  `"retryable-error"` outcome is returned for both "network genuinely
  unreachable" (a thrown fetch error) and "cloud responded but with a
  429/5xx" - two different facts a real operator would want to
  distinguish, but which collapse into the same shape - and deliberately
  left the whole field unwired rather than guess. Checkpoint 6D
  re-examined this and implemented the one half that requires no
  guessing at all: `SqliteSyncEngine` now takes an optional
  `onCloudConnected` callback, invoked only when `processOne()` observes
  an unambiguous `"created"`/`"duplicate"` outcome (a genuine successful
  round-trip to the cloud), and `Gateway`'s default constructor wires it
  to `this.health.setCloudConnectivity("CONNECTED")`. This is proven both
  by unit tests against the callback in isolation
  (`test/sync/sync-engine.spec.ts`) and by a cloud-integration test that
  constructs `Gateway` via its real default (no test-injected
  storage/syncEngine) path and drives a real sync against the live API
  (`test/cloud-integration/cloud-connectivity-health.spec.ts`). The
  `DISCONNECTED` direction remains deliberately unwired for exactly the
  reason Checkpoint 6B identified: the ambiguous `"retryable-error"`
  shape still cannot safely distinguish network-down from server-error,
  and a "sticky" `CONNECTED` that never reverts is a known, accepted
  limitation of this partial fix, not an oversight. Closing the rest
  properly is a scoped follow-up: either accept a `CloudClient`/
  `ClassifiedResponse` interface change to make network-vs-server-error
  distinguishable (which would also require updating exact-equality
  assertions in already-passing `http-response-classification.spec.ts`/
  `http-cloud-client.spec.ts`), or explicitly design the desired
  staleness/re-check semantics for a "last known" connectivity value
  before wiring a `DISCONNECTED` signal.
- **No requeue/backfill tooling for a gateway that was offline a long
  time.** The outbox mechanism itself has no limit on how long a
  transaction can sit `PENDING` (correct - "no data loss" per §13 - and
  proven directly by `test/cloud-integration/no-data-loss.spec.ts`), but
  there is no operator-facing view of "how many transactions are waiting,
  how old is the oldest one" beyond `pendingSyncCount` in the health
  snapshot and reading structured logs directly.
- **Gateway credentials are plaintext JSON, not a Windows-native secret
  store.** See §13d - same trust model as `apps/api/.env`, an accepted
  risk for this checkpoint, not a gap unique to the gateway. A real
  Windows deployment tightening this (DPAPI, Credential Manager, or
  similar) is a Phase 12 concern per `docs/gateway-decision.md` §9, not
  addressed here.
- **No raw-reading persistence.** See §3c - deliberately deferred, not
  forgotten.
- **No device-state persistence.** See §3b - deliberately deferred; the
  simulators added in Checkpoint 4 still don't create a concrete need for
  it (a `Device`'s connection state is entirely in-memory/process-local,
  and nothing yet needs to remember it across a restart).
- **`node:sqlite` is experimental.** See §2 - accepted risk, isolated
  behind one file, mitigated by the vendored-runtime deployment model.
- **Single-process assumption is real, not just documented.** The
  claim-then-conditional-update pattern would still be correct under
  multiple processes/connections (that's exactly what the conditional
  `UPDATE ... WHERE status = 'PENDING'` protects), but nothing has been
  tested under real concurrent access, and the gateway is not designed to
  run as more than one process against the same database file.
  **Concrete instance found and fixed in Checkpoint 6B:**
  `SqliteLocalStorage.init()`'s unconditional startup sweep
  (`recoverStaleProcessing(now, 0)`) assumed "this fresh process has made
  no claims yet, so every PROCESSING row is orphaned" - true for a real
  long-running gateway startup, but false for `main.ts`'s `--status`/
  `--version` diagnostic mode, which also calls the full `Gateway.start()`
  lifecycle and could run concurrently with an already-running service
  against the same `gateway.sqlite` file (e.g. an operator checking status
  on a live Windows service). That combination could incorrectly requeue
  a genuinely in-flight `PROCESSING` row. Fixed by threading an optional
  `{ recoverStaleProcessing?: boolean }` through
  `LocalStorage.init()`/`Gateway.start()`, defaulting to `true`
  everywhere except the one `--status`/`--version` call site in
  `main.ts`, which now passes `{ recoverStaleProcessing: false }`. This
  does not lose the `pendingSyncCount` signal (`countPendingOutbox()`
  already counts `PROCESSING` rows). Verified both by a new automated
  test (`test/storage/outbox-state-machine.spec.ts`) and by a manual
  end-to-end run of the compiled CLI in this sandbox (create a
  `PROCESSING` row directly, run `--status`, confirm the row is
  untouched and `pendingSyncCount` still reports it). This remains a
  single-process-at-a-time design otherwise - this fix only makes the
  one-shot diagnostic call safe to run alongside the real service, not a
  general multi-process storage engine.
- **Concurrent multi-device read order is an assumption, not a
  confirmed requirement (Checkpoint 4).** See §11 point 1 - revisit if a
  real operator workflow or hardware constraint later requires sequential
  or gated reads.
- **`capturedAt` = assembly time is an assumption, not a confirmed
  requirement (Checkpoint 4).** See §11 point 2 - revisit if the BRD or
  hardware spec later defines which device's timestamp (or some other
  rule) should be canonical.
- **`ReceptionCaptureWorkflow` has an operator trigger, but only a local
  CLI one, not a browser one.** UPDATED at Checkpoint 6E - this bullet
  originally said no trigger existed at all as of Checkpoint 4; that is no
  longer accurate. `pnpm simulate` (§15b) is a real "operator pressed a
  button" trigger (an Enter keypress), confirmed by the CEO as the correct
  trigger model (manual, not automatic polling). What remains missing is
  specifically a **browser-based** trigger reachable from the React web
  app - see §15d's STOP condition for exactly why that was not built
  (no IPC/HTTP path from the browser to a specific Gateway process exists
  anywhere in this repository).
- **Real serial transport exists; the ESSAE message protocol does not.**
  See §15c. `SerialTransport` can open a real RS232 connection at the
  CEO-confirmed parameters, but nothing in this repository can parse an
  ESSAE frame into a reading - `parser/parser.types.ts`'s `Parser`
  interface remains unimplemented, blocked entirely on a protocol
  specification that has not been supplied.
- **The `serialport` native addon has not been verified against the
  actual vendored Windows `node.exe`.** See §15c and
  `packaging/build-deployment.mjs`'s generated `app/node_modules/README.txt`
  - only verified in this Linux sandbox (linux-x64 prebuild); the vendored
  Windows runtime itself is still a placeholder file as of Checkpoint 6,
  so this is untested against the real deployment target, not just
  untested against real hardware.
- **No settings backend/UI exists anywhere in this repository.** See
  §15e. Baud rate is configurable today only via `gateway.config.json`
  (a locally-edited file), consistent with every other gateway
  configuration value - not via a web-facing settings page, since no
  settings module (API, DTO, persistence, or frontend) exists to build
  one on top of.

## 17. Local vs. cloud responsibility (edge/offline-first architecture, Checkpoint 7)

This section makes explicit what was implicit in every checkpoint above:
**local SQLite and the cloud's PostgreSQL are not redundant, and neither
one is a "temporary" stand-in for the other.** They are two different
databases with two different jobs, connected by exactly one directional
flow (local -> cloud, via the outbox/SyncEngine), plus one new read-only
local surface added this checkpoint so an operator can actually see the
local half of that picture. Nothing about §§1-16 above changes: this
section adds one new component (the local API + local dashboard) and
documents the resulting split of responsibility; it does not alter the
outbox, idempotency, or sync design in any way.

### 17a. What SQLite (`gateway.sqlite`) is for

- **Edge operational persistence.** The durable record of what happened
  at THIS centre, on THIS machine, the instant it happened - written
  synchronously, inside one SQLite transaction, before the operator's
  capture flow (§11) even considers itself "done" (§4). This is what
  makes the reception durable with **zero dependency on the cloud, the
  network, or anything outside this one process.**
- **Local transaction durability across restarts/crashes.** §8's restart
  recovery tests exist because this file - not PostgreSQL - is the thing
  that must survive a Windows service restart, a machine reboot, or a
  crash mid-sync without losing a single captured reception.
- **The outbox / pending-sync state.** `outbox_records` (§6/§7) is
  entirely local bookkeeping about *delivery attempts* to the cloud - it
  has no PostgreSQL equivalent and never will; it is meaningless outside
  this one gateway's own sync loop.
- **The source of truth for the LOCAL dashboard.** Every figure the new
  `LocalDashboardPage.tsx` (§17c) shows - today's local transaction count,
  today's local collection total, each transaction's local sync status -
  is read from this file, via the local API, and nothing else. This is
  precisely why the local dashboard keeps working with the cloud
  completely unreachable (§17d): it was never built to depend on cloud
  data in the first place.
- **Gateway-local state.** `gateway_metadata` (identity pinning, §3a) and
  anything else scoped to "this one install" belongs here, never in
  PostgreSQL.

### 17b. What PostgreSQL (via `apps/api`) is for

- **Central, consolidated, multi-centre persistence.** `MilkReceptionTransaction`
  rows across every chilling centre the organization operates, in one
  place - which is precisely what a single centre's local SQLite file
  structurally cannot be (and was never asked to be).
- **Cloud-side idempotency and the authoritative post-sync record.** §13b:
  once a local transaction is synced, its cloud-assigned ID
  (`cloud_transaction_id`, stamped back into `local_transactions`, §5) is
  what makes it a permanent, centrally-owned record - fit for
  cross-centre reporting, audit, and the org-wide `DashboardSummaryDto`
  the existing (cloud) `DashboardPage.tsx` already reads.
- **Central audit/history.** `AuditLogDto` (existing, cloud-only) and any
  future cross-centre reporting are built against PostgreSQL, not SQLite
  - SQLite has no audit table and this checkpoint does not add one.
  - Reception quality-validation outcome
  (ACCEPTED/HOLD/REJECTED - `TransactionStatus`) is decided **only** on
  the cloud side, during `POST /reception` (`apps/api/src/reception/
  reception.service.ts`), against quality rules that are themselves
  centrally managed. This is the one gap this checkpoint deliberately
  does **not** paper over: `CloudSendResult` (the SyncEngine's own
  return type from a sync attempt) never carries that outcome back to
  the gateway, and `local_transactions`/`outbox_records` has no column
  for it (confirmed against `src/storage/schema.ts`). The local API and
  local dashboard therefore show only **local sync state**
  (PENDING/PROCESSING/SYNCED/FAILED, from `outbox_records.status`), never
  ACCEPTED/HOLD/REJECTED - inventing that field locally would violate
  this task's explicit "do not invent data the schema does not contain"
  instruction. Closing this gap for real would mean either (a) having
  the cloud's reception-create response include the validation outcome
  and threading it back through `SyncEngine.processOne()` into a new
  `local_transactions` column, or (b) a later poll/pull of transaction
  outcomes from the cloud - both are real design decisions for a future
  checkpoint, not implemented here.
- **Central management of master data** (sources, vehicles, quality
  rules, users/RBAC) - all cloud-only today, unchanged by this
  checkpoint. The gateway does not cache or mirror any of this locally
  (confirmed: `local_transactions` stores only `source_id`/`vehicle_id`
  as bare integers, not names - §17c's transaction table shows raw IDs
  for exactly this reason, not as an oversight).

### 17c. The new local (edge) HTTP API

A minimal, **read-only** HTTP server, started by the long-running Gateway
process itself (`src/local-api/local-api-server.ts`, wired into
`main.ts`'s default long-running branch only - never the `--status`
diagnostic mode and never `scripts/simulate-capture-cli.ts`, both of which
also construct a `Gateway` against the same `gateway.sqlite` and would
otherwise race to bind the same port). Off by default; enabled via the new
optional `GatewayConfig.localApi` block (`enabled`, `port`, `bindAddress?`,
`accessToken` - see `config/gateway.config.example.json`).

Routes (all `GET`, all requiring `Authorization: Bearer <accessToken>`):

| Route | Returns |
|---|---|
| `/local/status` | `LocalGatewayStatusDto` - gateway/centre ID, version, uptime, service state, cloud/device connectivity, pending sync count. A direct read of the existing `HealthService.getSnapshot()` (§9/§16) - no new health tracking was added. |
| `/local/today` | `LocalTodaySummaryDto` - today's (IST calendar day, same convention as the cloud dashboard's `#dashboard-day-boundary` - see `local-day-bounds.ts`) local transactions, total quantity, and count. |
| `/local/transactions?limit=N` | `LocalTransactionsResponseDto` - the `N` most recent local transactions (default 50, capped 200), newest first. |

Trust domain, deliberately **not** the cloud's JWT:

- Gated by `localApi.accessToken`, a per-install shared secret compared
  with a constant-time check - **not** a cloud-issued JWT, and the cloud
  never sees this token. Distributing `JWT_SECRET` to every centre
  machine so it could verify cloud tokens locally would be a real
  regression to the cloud signing secret's blast radius (§13d); this is a
  smaller, more honestly-scoped trade-off for a read-only,
  single-centre, localhost/LAN-only surface.
- Binds to `127.0.0.1` by default (`bindAddress` can widen this to a LAN
  interface only if operators on other machines genuinely need it, per
  this checkpoint's explicit "bind only to localhost unless the
  architecture explicitly requires LAN access" instruction - not done by
  default).
- Never proxies to the cloud API, never accepts or forwards cloud
  credentials, never exposes a SQLite query surface (only the two new
  narrowly-scoped `LocalStorage` query methods below), and has no write
  routes at all.
- `Access-Control-Allow-Origin: *` is set deliberately (with `OPTIONS`
  preflight handling) - justified because there is no cookie-based
  session to protect, only an explicit `Authorization` header a
  cross-origin page cannot silently attach the way it could a cookie.

Two new read-only `LocalStorage`/`SqliteLocalStorage` methods back these
routes - `listLocalTransactionsInRange(startIso, endIso)` (for
`/local/today`'s day-bounded query) and `listRecentLocalTransactions(limit)`
(for `/local/transactions`) - both plain `SELECT`s against the existing
`local_transactions` table (§3c), no schema change.

### 17d. The local dashboard (`apps/web/src/pages/LocalDashboardPage.tsx`)

A new page (route `/local`, linked from the nav bar behind
`RECEPTION_VIEW` - the closest existing permission code, since no new
one was invented for this), **structurally separate from the existing
`DashboardPage.tsx`** (which remains exactly as it was, reading
`GET /dashboard/summary` from the cloud). The local dashboard:

- Talks **only** to the local API above (`src/api/localApiClient.ts`), via
  a per-browser URL + access token entered once and stored in
  `localStorage` (`cc-mc.localApiUrl` / `cc-mc.localApiToken`) - the same
  established pattern `api/client.ts` already uses for the cloud JWT, not
  a new mechanism.
- Polls `/local/status` and `/local/today` every 5 seconds and renders:
  today's total collection (kg), today's transaction count, pending cloud
  sync count, a recent-transactions table with each row's local sync
  status badge (PENDING/PROCESSING/SYNCED/FAILED), and an explicit,
  visible note that ACCEPTED/HOLD/REJECTED is not shown because the local
  schema does not have it (§17b).
- Shows a clear, persistent "offline / gateway unreachable" banner the
  moment a poll fails (gateway process down, or - just as importantly -
  the gateway is fine but *this browser* cannot reach it), and a
  "connected" banner (with the gateway's own reported `cloudConnectivity`)
  when a poll succeeds. This is the honest signal for "is my local view
  current," independent of whether the CLOUD is reachable.
- Never imports or touches `node:sqlite`, or anything from `apps/gateway`,
  directly - it only ever calls the local API's HTTP routes.

### 17e. Cloud authentication fix for local development

Root cause (established before this checkpoint's implementation began):
`config/gateway.config.json`'s `cloudApiBaseUrl` was left at the example
file's placeholder (`https://ccmc-api.example.org`), and `gatewayId` at
the placeholder `gw-example-001` - so a locally-running gateway had
nothing real to sync against, independent of anything about the
authentication *mechanism* itself (§13d's design was never the problem).
Fixed by editing the **local development** `gateway.config.json` (not the
committed example file, which correctly keeps placeholders for a reader to
replace) to point at the locally-running API
(`cloudApiBaseUrl: "http://localhost:3000"`, matching `apps/api/src/main.ts`'s
default `PORT` fallback) and giving this install a real, non-placeholder
`gatewayId` (`gw-blr-cc-01-local`). `cloudAuthEmail`/`cloudAuthPassword`
were already correct (the seeded Bangalore `GatewayService` account,
`gateway-blr-cc-01@ccmc.local` / `GatewayBLR@2026!sync` - §13d,
`apps/api/src/seed.ts`) and were not changed. No authentication mechanism
was weakened, replaced, or bypassed - this was purely a configuration
value pointing at the wrong place.

### 17f. Explicitly deferred: cloud -> gateway configuration sync

This checkpoint's data flow remains **one-directional**: local SQLite ->
outbox -> SyncEngine -> cloud PostgreSQL. A future "cloud pushes
configuration down to gateways" channel (e.g. centrally updating quality
rules, baud rate, or `localApi` settings across every centre without
touching each machine's `gateway.config.json` by hand) is a real, useful
idea raised during this checkpoint's planning, but is explicitly **out of
scope here** per this task's own instruction ("cloud → gateway config sync
explicitly deferred as a separate future design"). Building it now would
be exactly the kind of "random bidirectional sync" this checkpoint was
explicitly told not to build. Nothing in §17c-e assumes it exists.
