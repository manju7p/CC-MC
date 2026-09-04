# @cc-mc/gateway - CC-MC Local Device Gateway

Runs at each chilling centre, on a Windows machine, bridging local devices
(weighing scale, milk analyser) to the CC-MC cloud API. See
`docs/gateway-decision.md` (Windows service hosting decision) and
`docs/gateway-architecture.md` (internal architecture: module boundaries,
SQLite schema, atomicity/idempotency/outbox/retry/recovery design) at the
repository root for the full picture.

**Status: Checkpoint 5 (real cloud sync).** The gateway now makes real
HTTPS calls to the live NestJS API, with a real database-enforced
idempotency boundary, unattended authentication, and a full retry/
classification strategy. No real device adapters yet - see "What this
checkpoint does NOT include" below.

## What exists right now

- A minimal, long-running Node.js process (`src/main.ts`) that loads
  config, starts a `Gateway` instance, reports a structured health
  snapshot, and shuts down cleanly on `SIGINT`/`SIGTERM`.
- Clean module interfaces for the eventual full pipeline (`Device ->
  Transport -> Parser -> NormalizedReading -> LocalStorage -> SyncEngine ->
  CloudAPI`) - see `src/device`, `src/transport`, `src/parser`,
  `src/normalization`, `src/storage`, `src/sync`.
- **A real SQLite-backed `LocalStorage`** (`src/storage/sqlite-local-storage.ts`,
  using Node's built-in `node:sqlite`): migrations, a gateway-identity
  safeguard, and atomic local-transaction + outbox creation with
  DB-enforced idempotency. See `docs/gateway-architecture.md` for the full
  schema and design reasoning.
- **A real local `SyncEngine`** (`src/sync/sync-engine.ts`): claims
  eligible outbox items, delivers them through an injected `CloudClient`,
  and applies success/retry-with-backoff/terminal-failure back to SQLite.
- **A real `CloudClient`** (`src/sync/http-cloud-client.ts`): makes actual
  HTTPS calls to the NestJS API's `POST /reception`, using Node's built-in
  `fetch` (no new dependency). Backed by `CloudAuthTokenCache`
  (`src/sync/cloud-auth.ts`) for unattended login/cache/refresh, and
  `classifyHttpResponse()` (`src/sync/http-response-classification.ts`)
  for deterministic retryable/auth-retryable/terminal/idempotent-success
  classification. `Gateway` wires a real `HttpCloudClient` +
  `SqliteSyncEngine` by default and starts/stops it with the gateway
  process. See `docs/gateway-architecture.md` §13.
- A `DeviceManager`, and now (Checkpoint 4) real `Device` implementations
  to register with it: `WeighingScaleSimulator` and `MilkAnalyserSimulator`
  (`src/device/simulators/`), both simulator-backed with a real connection
  state machine and scriptable readings - no real hardware, no RS232, no
  Bluetooth. See `docs/gateway-architecture.md` §9.
- **A real capture workflow** (`src/capture/reception-capture-workflow.ts`):
  `ReceptionCaptureWorkflow` reads a scale + analyser device, validates
  and assembles a reception (`src/capture/reception-validation.ts`,
  mirroring the cloud API's `CreateReceptionDto` validation exactly),
  generates the local idempotency key exactly once, and persists it
  through `LocalStorage` - the full `Device -> normalized reading ->
  reception -> LocalStorage -> SyncEngine` path now runs end-to-end
  against simulators. See `docs/gateway-architecture.md` §11.
- A `HealthService` reporting gateway ID, centre ID, version, uptime,
  service state, cloud/device connectivity (still `UNKNOWN` - no cloud or
  device calls exist yet), and **a real `pendingSyncCount`** queried live
  from the SQLite outbox.
- A real, schema-verified WinSW v3 service configuration
  (`service/CCMCGateway.xml`) plus `install.ps1`/`uninstall.ps1`.
- A packaging script (`packaging/build-deployment.mjs`) that assembles and
  proves the deployment folder layout (binaries/runtime separate from
  persistent data).

## What this checkpoint does NOT include

- No real device adapters - no RS232, no Bluetooth, no manufacturer/model
  assumptions, no guessed baud rates/payload formats/device commands. Only
  simulator-backed `Device` implementations exist
  (`WeighingScaleSimulator`, `MilkAnalyserSimulator`). Real hardware
  protocol details are not yet known and must not be invented (see the
  project's strict rules) - `transport/`/`parser/` remain unimplemented,
  reserved for whenever real hardware adapters become possible.
- No `Gateway`/`main.ts` wiring that automatically invokes
  `ReceptionCaptureWorkflow` (no "operator pressed a button" or "poll on
  an interval" trigger) - Checkpoint 4 proves the capture flow works
  end-to-end when called directly (see the test suite); an actual
  operator-facing trigger is a later checkpoint's job, once that workflow
  is defined. See `docs/gateway-architecture.md` §14.
- No raw scale/analyser reading persistence, and no device
  configuration/state persistence - both deliberately deferred; see
  `docs/gateway-architecture.md` §3b/§3c for the reasoning.
- No manual "requeue a FAILED item" tool - see
  `docs/gateway-architecture.md` §14.
- `HealthService.cloudConnectivity` still always reports `UNKNOWN` -
  nothing wires real `HttpCloudClient`/`SqliteSyncEngine` outcomes back
  into it yet. The sync engine's own structured logs (`sync-engine`/
  `http-cloud-client` components) are the real signal for cloud
  reachability today; wiring that into the health snapshot is a natural
  small follow-up, not done this checkpoint since it wasn't asked for.
  See `docs/gateway-architecture.md` §13.
- No real RS232/Bluetooth device adapters, no Windows installer UI, no
  real device drivers - unchanged from Checkpoint 4, explicitly out of
  scope until a future checkpoint (see the project's strict rules).
- No real Windows binaries. `CCMCGateway.exe` and `node\node.exe` in a
  built deployment directory are clearly-labeled placeholder files - this
  sandbox's network egress blocks downloading the real artifacts (verified
  via `curl -sI` against both `github.com/winsw/winsw/releases` and
  `nodejs.org/dist/`, both returned `403 Forbidden`). See
  `packaging/build-deployment.mjs`'s header comment.
- No installer UI (`CCMC-Gateway-Setup.exe`) - Checkpoint 6/Phase 11.

## Commands

All commands run from the repository root via pnpm's `--filter`, since the
gateway is not wired into the root `package.json`'s script chain (kept
separate deliberately - the root scripts predate the gateway and this
checkpoint doesn't need to touch them):

```bash
# Install dependencies for this package only
pnpm install --filter @cc-mc/gateway

# Type-check and compile to dist/
pnpm --filter @cc-mc/gateway build

# Run the compiled gateway (long-running; needs a config file - see below)
pnpm --filter @cc-mc/gateway start

# Run directly from TypeScript source (no build step) - for local dev
pnpm --filter @cc-mc/gateway start:dev

# One-shot diagnostic: print a health snapshot and exit
pnpm --filter @cc-mc/gateway status

# Run the Jest test suite
pnpm --filter @cc-mc/gateway test

# Assemble and verify the Windows deployment folder layout (proof-of-concept
# packaging - produces apps/gateway/dist-deployment/, gitignored)
pnpm --filter @cc-mc/gateway package:deployment
```

## Configuration

The gateway reads a local JSON config file, resolved via:

1. the `GATEWAY_CONFIG_PATH` environment variable, if set, otherwise
2. `<current-working-directory>/config/gateway.config.json`

Copy `config/gateway.config.example.json` to `config/gateway.config.json`
and fill in this centre's real values before running:

```bash
cp config/gateway.config.example.json config/gateway.config.json
# edit gatewayId, centreId, cloudApiBaseUrl for this centre
```

`config/gateway.config.json` is gitignored (like `apps/api/.env`) - it's
environment-specific, not a secret, and not something to commit. Missing
or malformed required fields cause the gateway to fail fast at startup
with a clear error message (`src/config/config.loader.ts`), rather than
silently running with `undefined` values.

As of Checkpoint 5, the config file also holds `cloudAuthEmail` and
`cloudAuthPassword` - the gateway's own service-account credentials for
the cloud API (see "Cloud authentication" under `docs/gateway-architecture.md`
§13). These ARE credentials, unlike everything else in this file. The
gateway never persists an access token itself; it re-authenticates on
every process start and re-authenticates again automatically whenever the
cloud rejects a request with 401. The trust model matches
`apps/api/.env`: a local, gitignored, plaintext JSON file the operator
edits by hand, protected by OS-level file permissions on the deployment
machine, not by application-level secret management. See
`docs/gateway-decision.md` §9.

## Windows service (WinSW)

`service/CCMCGateway.xml` is a real WinSW v3 configuration, verified
element-by-element against WinSW's own documentation - not invented. It
has **not** been installed or run against a real Windows Service Control
Manager: this development environment is Linux-only. `install.ps1` and
`uninstall.ps1` operate on an already-assembled deployment directory (see
`packaging/build-deployment.mjs`) and are reviewed for correctness, but
likewise unverified on real Windows. See `docs/gateway-decision.md` §10
("Known limitations") - real-Windows verification of
install/boot-autostart/crash-restart/stop/uninstall is a Checkpoint 10
task.

## Configuration - `dataDirectory` (Checkpoint 3)

Every config file now also needs `dataDirectory`: the folder the SQLite
database (`gateway.sqlite`) lives in, matching the deployment layout's
`data\` folder (`docs/gateway-decision.md` §1). Kept separate from `app\`
and `logs\` so an application upgrade never touches it. See
`config/gateway.config.example.json`.

## Testing

`pnpm --filter @cc-mc/gateway test` runs the fast default suite (152
tests, 16 files) - fully hermetic, no real network or database required
(the cloud-integration suite below is deliberately excluded from this
run, via `jest.config.js`'s `testPathIgnorePatterns`):

- `test/gateway.lifecycle.spec.ts` - `Gateway` start/stop state
  transitions and health snapshot fields (now including a real
  `pendingSyncCount`); `config.loader`'s fail-fast validation; `DeviceManager`
  register/list/unregister/connectAll/disconnectAll against a fake `Device`.
- `test/storage/schema-migrations.spec.ts` - migrations create the
  expected tables, are recorded, and are idempotent across a restart.
- `test/storage/gateway-metadata.spec.ts` - the gateway-identity
  safeguard pins on first init and refuses a mismatched restart.
- `test/storage/local-transactions.spec.ts` - atomic create, idempotent
  duplicate handling (DB-level, not just app-level), a forced real
  mid-transaction failure proving full rollback, and multi-transaction
  restart survival.
- `test/storage/outbox-state-machine.spec.ts` - claim/synced/retry/failed
  transitions, eligibility filtering, and stale-PROCESSING recovery.
- `test/storage/backoff.spec.ts` - the backoff function in isolation.
- `test/sync/sync-engine.spec.ts` - `SqliteSyncEngine.tick()` against a
  fake `CloudClient`: success, duplicate, retry-with-backoff (and no
  hammering before the backoff elapses), terminal failure, max-attempts
  exhaustion, batch processing, and stale-item recovery mid-run.
- `test/restart-recovery/subprocess-kill.spec.ts` - **real** process-kill
  tests: a genuine child process is spawned, reaches a synchronized point
  mid-transaction or mid-claim, and is sent a real `SIGKILL` - not a
  mock. See `docs/gateway-architecture.md` §8.
- `test/storage/idempotency-conflict.spec.ts` (Checkpoint 4) - same-key
  same-payload retries return cleanly; same-key different-payload throws
  `IdempotencyKeyConflictError` naming the conflicting field(s); multiple
  conflicting fields are all reported; a `capturedAt`-only conflict is
  still caught; a genuinely identical payload does not false-positive.
  See `docs/gateway-architecture.md` §5.
- `test/device/weighing-scale-simulator.spec.ts` and
  `milk-analyser-simulator.spec.ts` (Checkpoint 4) - normal, repeated,
  invalid, unavailable (both connect-failure and mid-session drop), and
  delayed-response scenarios against the real connection state machine;
  a determinism check for the seeded demo-only random reading. See
  `docs/gateway-architecture.md` §9.
- `test/capture/reception-validation.spec.ts` (Checkpoint 4) -
  `assembleReception()`'s validation rules field-by-field, including that
  `temperature` has no minimum (matching the cloud DTO) and that every
  violation is reported at once, not just the first.
- `test/capture/reception-capture-workflow.spec.ts` (Checkpoint 4) - all
  seven numbered failure semantics from the Checkpoint 4 brief (device
  unavailable, invalid reading, outbox-exists-on-success, cloud-unavailable
  durability, retry reuses the same transaction/key, duplicate capture
  creates nothing extra, and a same-key-different-payload conflict is
  explicit rather than silent), plus the concurrent-read timing proof, the
  assembly-time-`capturedAt` proof, a transaction->outbox integration test
  against the real `SqliteSyncEngine`, and a restart/recovery integration
  test. See `docs/gateway-architecture.md` §11.
- `test/sync/cloud-auth.spec.ts` (Checkpoint 5) - `decodeJwtExpiryMs`
  edge cases, and `CloudAuthTokenCache`: caches and reuses a valid token,
  proactively refreshes once past the configured margin, `invalidate()`
  forces a fresh login, concurrent `getToken()` calls single-flight into
  one login, an undecodable token still gets a conservative cache window,
  and no call ever logs the password/email/token.
- `test/sync/http-response-classification.spec.ts` (Checkpoint 5) - every
  documented status-code mapping (2xx/401/409/429/5xx/other 4xx), message
  extraction (string and class-validator array forms), and three
  regression tests for a real bug this suite caught: the real API returns
  HTTP 201 for BOTH a fresh create and an idempotent duplicate, so
  `created` vs. `duplicate` must be read from the response body's
  `outcome` field, never inferred from the status code alone.
- `test/sync/http-cloud-client.spec.ts` (Checkpoint 5) - `HttpCloudClient`
  against a scripted fake `fetch`: successful creation, idempotent
  duplicate, 409 conflict, 500 retryable, a network-level rejection, a
  real timeout (genuine `AbortController` firing, asserted by elapsed
  time), 401 -> re-auth -> same-key retry succeeding, two 401s in a row
  being terminal (not an infinite loop), never logging secrets across a
  401 retry, and a regression test for a cross-realm `instanceof Error`
  bug Jest's test environment exposed in real network-error handling
  (see `docs/gateway-architecture.md` §13).

### Cloud-integration suite (Checkpoint 5) - `pnpm test:cloud`

A SEPARATE Jest project (`test/cloud-integration/jest-cloud.config.js`),
deliberately excluded from the default `pnpm test` run because it needs a
real PostgreSQL instance and spawns the real compiled NestJS API as a
child process. Run it explicitly:

```bash
# from apps/gateway - needs a running local Postgres (postgres/postgres,
# localhost:5432) and apps/api already built (pnpm --filter @cc-mc/api build)
pnpm test:cloud
```

`test/cloud-integration/support/live-cloud-fixture.ts` resets/seeds a
dedicated `ccmc_gateway_test` database (separate from `apps/api`'s own
`ccmc_test`) and starts the real `apps/api/dist/main.js` on port 3901
with a deliberately short (3s) JWT expiry, so the auth-expiry test can
prove a real token expiring without a mocked clock. Nothing in this suite
is mocked - real HTTP, real Postgres, real gateway SQLite:

- `00-smoke.spec.ts` - the fixture itself works end-to-end.
- `critical-failure-scenario.spec.ts` (**mandatory**) - a lost response
  after the cloud already committed does not create a duplicate.
- `concurrent-duplicate.spec.ts` (**mandatory**) - two, and ten,
  genuinely simultaneous requests with the same `localIdempotencyKey`
  produce exactly one row/audit event; a same-key-different-payload race
  produces one success and one explicit conflict.
- `no-data-loss.spec.ts` - a transaction captured while the cloud is
  unreachable survives durably in SQLite and syncs on the next real
  opportunity once the cloud is reachable again.
- `network-failure.spec.ts` - the 7 required scenarios (unreachable,
  timeout, dropped connection, 500, 401, 409, successful retry after a
  previous failure), reproduced with real TCP servers and the real live
  API, not mocks.
- `centre-security.spec.ts` (**mandatory**) - a gateway scoped to one
  centre cannot create a reception for the other seeded centre, in both
  directions, through the real `HttpCloudClient`/live API.
- `auth-expiry.spec.ts` (**mandatory**) - a real, actually-expired JWT
  triggers a real 401, and `HttpCloudClient` re-authenticates and
  completes the SAME logical transaction (same `localIdempotencyKey`,
  exactly one row/audit event) without caller intervention.

See `docs/gateway-architecture.md` §13 for the full design writeup.

`@types/node` is pinned to `^22.10.2` in this package specifically (rather
than matching `apps/api`'s `^20.14.15`) because `node:sqlite`'s type
definitions were only added in that later version - a concrete,
documented reason, not a style drift.
