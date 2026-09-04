# CC-MC LOCAL RUNBOOK

**Verification method:** every claim below was produced by directly reading the current repository's files (`package.json` files, `.env`/`.env.example`, `tsconfig.json` files, `src/main.ts`, `src/config/*`, `src/seed.ts`, controller/entity source, `service/*`, `packaging/*`, the shared-types package, and the three `README.md`/`docs/*.md` files), and by **actually running** the commands below against a real local PostgreSQL instance and a real local API — not from memory of an original plan, and not from assumption. Where a claim was verified by direct execution in this update (packaging, the new simulator CLI, the full test suites, the exact Postgres/SQLite schema), that is stated explicitly, together with the environment it was run in. Where the repository does not contain something, this document says so explicitly with a file reference, rather than inventing it.

This revision adds: a fix for the `pnpm package:deployment` / `spawnSync npx ENOENT` failure (§10), a brand-new interactive **Simulator Capture CLI** (`pnpm simulate`, §6) that gives an operator a real trigger for a full simulator→cloud capture (previously only possible via a non-interactive demo script), corrected PostgreSQL column names throughout (§7 — the previous revision of this document used snake_case column names that do not exist in the real schema), and a new section for inspecting the gateway's local SQLite state directly (§8). All prior content has been kept; nothing here overwrites the architecture already implemented in Checkpoints 1–6.

---

## Architecture Overview (background)

```
┌─────────────────────┐        HTTP (proxied /api → :3000)        ┌──────────────────────┐
│  React frontend      │ ─────────────────────────────────────────▶│  NestJS API           │
│  (Vite, :5173)       │◀─────────────────────────────────────────│  (:3000)              │
└─────────────────────┘        JSON responses, JWT auth            └──────────┬───────────┘
                                                                               │ TypeORM
                                                                               ▼
                                                                    ┌──────────────────────┐
                                                                    │  PostgreSQL 16        │
                                                                    │  (ccmc_dev, :5432)    │
                                                                    └──────────▲───────────┘
                                                                               │ HTTPS/HTTP
                                                                               │ POST /auth/login
                                                                               │ POST /reception
┌─────────────────────┐   scale/analyser    ┌──────────────────┐              │
│ WeighingScale/       │ ───────readings────▶│ ReceptionCapture  │             │
│ MilkAnalyser         │                     │ Workflow          │            │
│ Simulators           │                     └────────┬─────────┘            │
└─────────────────────┘                                │ persist              │
                                                         ▼                    │
                                              ┌──────────────────┐            │
                                              │ SQLite            │            │
                                              │ (gateway.sqlite)  │            │
                                              │ local_transactions│            │
                                              │ + outbox_records  │            │
                                              └────────┬─────────┘            │
                                                         │ SqliteSyncEngine     │
                                                         │ .tick() every 15s    │
                                                         └──────────────────────┘
```

The frontend never talks to the gateway directly, and the gateway never talks to the frontend directly — they only meet through the shared PostgreSQL data the API exposes. The long-running gateway process (`pnpm start`/`pnpm start:dev`, no args) does **not** wire any simulator into this diagram's left branch by itself. The new Simulator Capture CLI (§6) is what actually drives the left branch, in-process, sharing the same `gateway.sqlite` file — see §6 for exactly why that's still a genuine, non-fake integration and not a second data path.

**No global API route prefix** — every endpoint is at the server root (e.g. `http://localhost:3000/auth/login`, not `/api/auth/login`); the frontend's own `/api` prefix is a Vite dev-proxy convention, stripped before reaching the API. **No health endpoint** exists anywhere in `apps/api` — verified by grepping every `*.controller.ts`. **No Redis, message broker, second database engine, or Docker requirement** exists anywhere in `package.json`/source.

---

## 1. PREREQUISITES

1. **Node.js version.** Neither `apps/api/package.json` nor `apps/gateway/package.json` declares an `engines` field (checked directly — absent from both). The root `package.json`'s `engines.node` is `">=20"` — the only *declared* constraint in the repository. However, the gateway uses Node's built-in `node:sqlite` module (`apps/gateway/src/storage/sqlite-local-storage.ts`) and depends on `@types/node: ^22.10.2` (`apps/gateway/package.json`) — `node:sqlite` is only present (as an experimental, flag-free module) from Node 22 onward. **Use Node 22.x for the whole toolchain.** This was verified this session by actually running the gateway, its full test suite, and the new simulator CLI under Node `v22.22.2` — all passed (see §12/§6). Do not use Node 20 for the gateway even though the root `engines` field would technically allow it; `node:sqlite` will not exist.
   ```powershell
   node --version
   ```
   Expect `v22.x.x`.

2. **pnpm.**
   ```powershell
   pnpm --version
   ```
   If this fails with "not recognized," run `corepack enable` (ships with Node ≥16.9) then re-check, or `npm install -g pnpm`. This repository's own tooling was exercised this session with pnpm `11.22.0` (via `corepack pnpm`) — no specific version is pinned by the repo (no `packageManager` field in root `package.json`), so this is a data point, not a hard requirement.

3. **PostgreSQL 16**, already installed per your environment — verify it's actually running and reachable:
   ```powershell
   Get-Service -Name "postgresql*"
   ```
   Expect at least one service with `Status: Running`. If none appear, check `Get-Service | Where-Object {$_.DisplayName -like "*postgres*"}` in case it runs under a different service name.

4. **`psql`/`createdb` on PATH:**
   ```powershell
   psql --version
   ```
   If "not recognized," the official EnterpriseDB Windows installer for PostgreSQL 16 places these at `C:\Program Files\PostgreSQL\16\bin\psql.exe` (and `createdb.exe`) by default — confirm this matches your machine (File Explorer, or Windows Settings → Apps → "PostgreSQL" → install location) before using it; it is not verifiable from this repository. Then either:
   ```powershell
   $env:Path += ";C:\Program Files\PostgreSQL\16\bin"
   ```
   or call the binaries by full path every time (used throughout this document as the safe default).

5. **Required env/config files** — what must exist before anything will start:
   - `apps/api/.env` — **already present in the repository** with working local-dev values (see §2/§3). Only edit it if your Postgres credentials differ from `postgres`/`postgres`@`localhost:5432`.
   - `apps/gateway/config/gateway.config.json` — **does NOT exist yet**; only `gateway.config.example.json` does. You create it in §2/§5. It is `.gitignore`'d (root `.gitignore`'s last rule) so creating it locally will never accidentally get committed.
   - `apps/web` needs **no** environment file at all — verified: no `.env`/`.env.example`/`import.meta.env.*` reference exists anywhere under `apps/web`.

6. **Git** (only if you still need to clone/update — optional if already cloned):
   ```powershell
   git --version
   ```

---

## 2. INSTALLATION

Run from `<repo-root>` (the folder containing this repo's root `package.json`/`pnpm-workspace.yaml`).

```powershell
# 1. Install all workspace dependencies (apps/api, apps/gateway, apps/web, packages/shared-types)
cd <repo-root>
pnpm install
```

**If `pnpm install` stops with `[ERR_PNPM_IGNORED_BUILDS] Ignored build scripts: @nestjs/core@..., bcrypt@..., esbuild@...`** — this is a genuine, pre-existing condition of this repository's `pnpm-workspace.yaml` (pnpm's newer versions require explicit approval before running any dependency's install/postinstall script, and this repo's `onlyBuiltDependencies` list needs a matching `allowBuilds` approval to actually take effect). Run:
```powershell
pnpm approve-builds --all
```
then re-run `pnpm install`. This is a one-time, per-machine step; `pnpm approve-builds` persists the approval into `pnpm-workspace.yaml` itself, so you will not need to repeat it on subsequent installs on the same machine/checkout. (See §10/§12 — this exact sequence was needed and verified in the environment this update was produced in.)

```powershell
# 2. Build the workspaces the root build script covers (shared-types, api, web —
#    NOT the gateway; the gateway is deliberately excluded from the root
#    "build" script (apps/gateway has no entry in root package.json's
#    scripts.build) and is built separately, see §5).
pnpm build

# 3. Build the gateway separately
cd apps\gateway
pnpm build
cd ..\..

# 4. Run API database migrations (apps/api/src/migrations/1787224364198-InitSchema.ts)
cd apps\api
pnpm migration:run

# 5. Seed the API database (roles, permissions, 2 centres, 6 users incl. 2 gateway
#    service accounts, quality rules, sample sources/vehicles). Idempotent — safe
#    to re-run against an already-seeded database, it will not duplicate rows.
pnpm seed
cd ..\..
```

**Verify the seed actually landed** (no dedicated "verify seed" command exists in the repo, so a direct read-only query is the way to check — see §7 for the full, schema-verified query set):
```powershell
$env:PGPASSWORD = "postgres"
& "C:\Program Files\PostgreSQL\16\bin\psql.exe" -U postgres -h localhost -d ccmc_dev -c "SELECT id, code, name FROM chilling_centres ORDER BY id;"
```
Expect:
```
 id |    code    |            name
----+------------+------------------------------
  1 | BLR-CC-01  | Chilling Centre - Bangalore
  2 | MYS-CC-01  | Chilling Centre - Mysore
```
If your `id` values differ (e.g. a non-fresh, previously-reseeded database), use the actual values shown here for `centreId`/`sourceId`/`vehicleId` throughout this document instead of assuming `1`.

---

## 3. START API

```powershell
# Terminal: keep this running for the rest of this session
cd <repo-root>\apps\api
pnpm start:dev
```

**Expected successful output** (from `apps/api/src/main.ts`'s own `console.log`, after Nest's own module-initialization log lines):
```
CC-MC API listening on port 3000
```

**Exact port:** `3000` (`apps/api/.env`'s `PORT=3000`; `main.ts` falls back to `3000` if unset).

**How to verify it's actually alive** — there is no health endpoint, so call the one endpoint that needs no auth at all, with a real seeded credential:
```powershell
Invoke-RestMethod -Uri "http://localhost:3000/auth/login" -Method Post -ContentType "application/json" -Body '{"email":"operator1@ccmc.local","password":"Operator@12345"}'
```
A running, correctly-seeded API returns a JSON body with `accessToken` and a `user` object. A connection error means the API isn't listening; a `401`/`400` with this exact credential means the API is alive but the database isn't seeded — re-check §2.

---

## 4. START FRONTEND

```powershell
# Terminal: keep this running alongside the API
cd <repo-root>\apps\web
pnpm dev
```

- **Port:** `5173` — hardcoded in `apps/web/vite.config.ts`'s `server.port`.
- **API connection:** automatic via Vite's dev proxy (`/api/*` → `http://localhost:3000`, `/api` prefix stripped) — no configuration needed, provided the API (§3) is already running.
- **Login page:** `http://localhost:5173/login` (an unauthenticated visit to `/` redirects here).
- **Seeded login credentials** (from `apps/api/src/seed.ts` — **DEVELOPMENT ONLY, never use these in a real deployment**):

  | Email | Password | Role | Centre |
  |---|---|---|---|
  | `admin@ccmc.local` | `Admin@12345` | Admin | All centres |
  | `manager1@ccmc.local` | `Manager@12345` | Manager | Bangalore |
  | `operator1@ccmc.local` | `Operator@12345` | Operator | Bangalore |
  | `operator2@ccmc.local` | `Operator@12345` | Operator | Mysore |
  | `gateway-blr-cc-01@ccmc.local` | `GatewayBLR@2026!sync` | GatewayService (machine-only, `RECEPTION_CREATE` only) | Bangalore |
  | `gateway-mys-cc-01@ccmc.local` | `GatewayMYS@2026!sync` | GatewayService (machine-only, `RECEPTION_CREATE` only) | Mysore |

  Do **not** log into the frontend UI as either `gateway-*@ccmc.local` account — they hold only `RECEPTION_CREATE` and are meant solely for the gateway's/simulator CLI's own `POST /auth/login` calls (§6); logging in as one in a browser shows an essentially empty UI and muddies the audit trail's "human vs. gateway" distinction.

---

## 5. START GATEWAY

**The sharpest distinction in this whole document: "the gateway process is running" and "the gateway has captured a milk reception" are different things.** The long-running gateway process, by itself, registers zero simulators and creates zero receptions — see below.

### 1. Configure it (one-time, per machine)
```powershell
cd <repo-root>\apps\gateway
copy config\gateway.config.example.json config\gateway.config.json
notepad config\gateway.config.json
```
Fill in with these real, verified-working local values:
```json
{
  "gatewayId": "gw-blr-cc-01-local",
  "centreId": 1,
  "cloudApiBaseUrl": "http://localhost:3000",
  "logDirectory": "./logs",
  "dataDirectory": "./data",
  "cloudAuthEmail": "gateway-blr-cc-01@ccmc.local",
  "cloudAuthPassword": "GatewayBLR@2026!sync",
  "localApi": {
    "enabled": true,
    "port": 4100,
    "bindAddress": "127.0.0.1",
    "accessToken": "local-dev-only-token-blr-cc-01"
  }
}
```
The first seven fields (through `cloudAuthPassword`) are required — `src/config/config.loader.ts` throws a clear `ConfigValidationError` naming the exact missing/invalid field if any are absent or the wrong type (`centreId` must be a bare JSON integer, not a quoted string). `serial` and `localApi` are both OPTIONAL blocks (omit either entirely if not needed), but once either key is present, all of its own required sub-fields must be present too — see `config/gateway.config.example.json`'s inline `_comment` fields for the exact shape of each. **This is the one config file both the long-running gateway (below) and the new Simulator Capture CLI (§6) read** — they are not two separate configurations; using the same file is what makes them share the same `gateway.sqlite` identity (see §6's design notes on why this matters). `localApi.accessToken` above is a local-dev-only value for this checkout, not a real secret — generate a different random value for any real deployment (see the example file's comment for a one-liner to do that).

### 2. Build it (optional for dev — `start:dev` uses `ts-node`, no build needed)
```powershell
pnpm build
```

### 3. Run it interactively
```powershell
pnpm start:dev
```
or, after `pnpm build`: `pnpm start`.

**What this starts** (verified line-by-line in `src/gateway.ts`'s `start()`): opens/creates `gateway.sqlite` in `dataDirectory`, runs its startup stale-`PROCESSING` recovery sweep, calls `DeviceManager.connectAll()` (a no-op today — **no device is ever registered in this codepath**, verified: `src/main.ts` never calls `gateway.devices.register(...)`), starts the real `SqliteSyncEngine` on its 15-second poll loop against your configured `cloudApiBaseUrl`, sets `serviceState: "RUNNING"`, and stays alive until `Ctrl+C`.

**By itself this process will never create a milk reception** — it has nothing to sync until a local transaction/outbox row exists in `gateway.sqlite`, and nothing in this codepath ever creates one. It will faithfully sync anything later inserted into that same file by another process — which is exactly what §6's CLI does.

### 4. Check status
```powershell
pnpm status
```
Prints one JSON health snapshot (a separate, one-shot process safe to run alongside a live long-running instance — both pass `{recoverStaleProcessing: false}` for this exact reason, see `src/storage/storage.types.ts`'s `init()` doc comment):
```json
{
  "gatewayId": "gw-blr-cc-01-local", "centreId": 1, "version": "0.1.0",
  "startedAt": "...", "uptimeSeconds": 0, "serviceState": "RUNNING",
  "cloudConnectivity": "UNKNOWN", "deviceConnectivity": "UNKNOWN", "pendingSyncCount": 0
}
```

### 5. Stop it
`Ctrl+C` — triggers documented clean shutdown (`gateway.ts`'s `stop()`).

### 6. Logs
One JSON line per event to stdout (`src/logging/logger.ts`). No separate log file in dev-run mode.

### 7. SQLite file
`<dataDirectory>/gateway.sqlite` (e.g. `apps\gateway\data\gateway.sqlite`) — created on first `start()`. See §8 to inspect it directly.

### 8–10. Health field semantics
- `serviceState: "RUNNING"` means the startup sequence finished and the steady-state loop is active — it says nothing about whether a reception has ever been captured.
- `cloudConnectivity` stays `"UNKNOWN"` until the sync engine completes at least one real successful sync (a `"created"`/`"duplicate"` outcome), then flips to `"CONNECTED"` and **never flips back** — the current error shape can't safely distinguish "network down" from "cloud returned an error," so it deliberately never reports `"DISCONNECTED"` (documented gap, `docs/gateway-architecture.md`).
- `pendingSyncCount` is a real count of `PENDING`/`PROCESSING` outbox rows.
- `deviceConnectivity` is **permanently `"UNKNOWN"`** — no code path anywhere calls `setDeviceConnectivity(...)`; the only call site is its own definition in `src/health/health.service.ts`. The simulator CLI in §6 does not change this either.

### 11. Do not run two long-running instances against the same `dataDirectory`
Aside from the specifically-safe `--status`/simulate-CLI case (both opt out of the stale-sweep), two long-running gateways contending for the same `gateway.sqlite` is not a supported configuration.

### 12. Local (edge) HTTP API — only starts if `localApi.enabled` is set
If `config.localApi.enabled` is `true` (see the config sample above), the **long-running** `pnpm start:dev`/`pnpm start` process (only — never `--status`, never `pnpm simulate`, both would race it for the same port) also starts a small read-only HTTP server on `bindAddress:port` (default `127.0.0.1:4100` in the sample above). This is what the frontend's **Local Dashboard** page (§9a) talks to. Look for a log line like:
```json
{"timestamp":"...","level":"info","component":"gateway.local-api","message":"Local API listening","bindAddress":"127.0.0.1","port":4100}
```
If the port is already in use, the gateway logs the failure and **keeps running anyway** (`src/main.ts` explicitly does not let a local-API bind failure take down device capture/cloud sync) — check the log for `"Failed to start local API"` if the dashboard can't connect.

---

## 6. SIMULATOR CAPTURE

### Why this exists
Before this update, the only way to exercise a real simulator→SQLite→outbox→sync→cloud capture was `apps/gateway/scripts/simulate-e2e-demo.ts` — a fixed, non-interactive proof script (env-var driven, one hardcoded scenario), not an operator-usable command, not reachable from `pnpm start`, the CLI, or the frontend. This section documents the new **`pnpm simulate`** command (`apps/gateway/scripts/simulate-capture-cli.ts`), which is the missing operator trigger: an interactive CLI where you type reception fields once, press Enter, and it runs one real capture through the exact same production code path.

`simulate-e2e-demo.ts` still exists and still works (see the previous revision of this document, kept below in Appendix A) — it was not removed or altered. `pnpm simulate` is additive.

### Naming convention
`apps/gateway/package.json` already used short, single-word script names for every gateway action (`build`, `start`, `start:dev`, `status`, `test`, `test:cloud`, `package:deployment`). Following that convention, the new script is registered as plain **`simulate`**, run as:
```powershell
cd <repo-root>\apps\gateway
pnpm simulate
```
(equivalent to `npx ts-node scripts/simulate-capture-cli.ts` from `apps/gateway`, exactly like `start:dev` is `ts-node src/main.ts`.)

### Architecture — read this before assuming otherwise
There is **no IPC or HTTP mechanism anywhere in this repository** for a separate process to talk to an already-running `pnpm start`/`pnpm start:dev` gateway — `src/main.ts` exposes nothing to attach to (verified: no server, no socket, no named pipe is ever opened by the long-running gateway). This CLI therefore implements **Option A**: it constructs its own `Gateway`/`SqliteLocalStorage`/`SqliteSyncEngine`/`HttpCloudClient` in-process, using the **exact same `gateway.config.json`** (via the same `loadConfig`/`resolveConfigPath` the long-running gateway and `--status` use) — and therefore the exact same `gateway.sqlite` file. That shared file is what makes this cooperative, not a fake second data path: if a real `pnpm start:dev` gateway happens to be running concurrently against the same config, this CLI's capture becomes a genuine new row in the SAME outbox that process's own `SyncEngine` polls every 15 seconds. The CLI does not rely on that coincidence, though — it drives its own `SqliteSyncEngine.tick()` once, immediately, so a single run is a complete, self-contained proof with an immediate result whether or not another gateway process happens to be running. **No new IPC protocol, HTTP endpoint, or web application was invented to make this look "connected" — it deliberately is not connected that way, because the repository has no mechanism for that today.**

Concurrency safety: like `--status`, this CLI calls `storage.init({ recoverStaleProcessing: false })` — it may run at the same moment as a live long-running gateway against the same `gateway.sqlite`, so it opts out of the unconditional "every PROCESSING row is orphaned" startup sweep (the same hazard `--status` was already built to avoid).

### Exact fields — every one is real, none invented
| Prompt | Real field it becomes | Source |
|---|---|---|
| Source ID (default `1`) | `sourceId` | `CreateReceptionRequest`/`CreateReceptionDto` |
| Vehicle ID (default `1`) | `vehicleId` | same |
| Quantity (kg) (default `450`) | `quantityKg` | same — **mass in kilograms, not liters.** There is no "Volume (L)" field anywhere in this system's domain model; a literal reading of a mockup using liters would have been wrong and was corrected here. |
| Fat (%) (default `4.2`) | `fat` | same |
| SNF (%) (default `8.6`) | `snf` | same |
| Temperature (°C) (default `4.0`) | `temperature` | same |

`centreId` is **not** prompted — it comes from `gateway.config.json`, exactly like the real gateway; a gateway serves one centre, and this CLI does not invent a "switch centre" capability the real architecture doesn't have.

**Why Source ID/Vehicle ID default to `1`/`1` instead of being looked up live:** this is a real, load-bearing RBAC fact, not a shortcut. `GET /sources` and `GET /vehicles` both require `SOURCE_VIEW`/`VEHICLE_VIEW` (`apps/api/src/sources/sources.controller.ts`, `vehicles.controller.ts`), permissions the seeded `GatewayService` role deliberately does **not** hold — it holds only `RECEPTION_CREATE` (`apps/api/src/seed.ts`). A gateway credential that could also read source/vehicle master data would be a wider blast radius than "create receptions for my one centre," so the gateway/CLI genuinely cannot call those two endpoints (would get `403`). The only defaults this CLI has any basis for are the already-seeded reference rows for centre 1 (`SRC-BLR-001`/id `1`, `KA01AB1234`/id `1` — verify with the query in §7), shown as a labeled default, always overridable by typing a different id. `GET /centres` requires only authentication (`@UseGuards(JwtAuthGuard)`, no `@RequirePermission` — verified in `centres.controller.ts`), so the gateway credential *can* legitimately call that one endpoint, which is why the CLI's header can show a real friendly centre name.

### Internal flow (exactly what runs, in order)
1. Load `gateway.config.json` (same loader as the long-running gateway).
2. Construct real `SqliteLocalStorage` / `HttpCloudClient` / `SqliteSyncEngine` / `Gateway`, call `gateway.start({ recoverStaleProcessing: false })` (opens/creates the same `gateway.sqlite`).
3. Best-effort `POST /auth/login` + `GET /centres` to show a friendly centre name in the header (falls back to showing the bare numeric `centreId` if this fails for any reason — never blocks the tool).
4. Prompt for the six fields above (Enter accepts the shown default), then one more "Press ENTER to capture...".
5. Construct a fresh `WeighingScaleSimulator`/`MilkAnalyserSimulator`, connect them, queue exactly the entered scale/analyser reading via `setNextReadings(...)`.
6. Call the **real** `ReceptionCaptureWorkflow.captureReception(...)` — this is the same class the automated test suite and `simulate-e2e-demo.ts` use; it generates **exactly one** `localIdempotencyKey`, and persists the local transaction + outbox row via the real `SqliteLocalStorage`.
7. Call `syncEngine.tick()` once (the same public method the long-running gateway's 15-second poll loop calls internally) so the run has an immediate, self-contained result.
8. Re-read the outbox record, local transaction, and health snapshot straight back out of SQLite, and print the final report.
9. Ask "Capture another? (y/N)" — loop or exit; `finally` always closes the prompt and calls `gateway.stop()`.

### What the CLI cannot report, and why (do not expect an ACCEPTED/HOLD field)
The cloud's quality-validation ACCEPTED/HOLD decision is **never returned to the gateway in any form** — `CloudSendResult` (`apps/gateway/src/sync/cloud-client.types.ts`) is exactly `{outcome: "created"|"duplicate", cloudTransactionId} | {outcome: "retryable-error"|"terminal-error", message}`. There is no status field in that contract, by design — the gateway's `HttpCloudClient` calls `POST /reception` and never calls `GET /reception/:id` (which would need `RECEPTION_VIEW`, another permission `GatewayService` does not hold). So the CLI's report shows the real sync outcome and cloud transaction id, but explicitly does **not** claim an ACCEPTED/HOLD status — it tells you to check the frontend's Reception page for that (§9).

### Example interaction (this is a real transcript, captured this session)
```
----------------------------------------------------
CC-MC Gateway Simulator
----------------------------------------------------
Centre:     Chilling Centre - Bangalore (BLR-CC-01)
Gateway ID: gw-blr-cc-01-local
Cloud API:  http://localhost:3000

Enter reception details (press Enter to accept the default shown):

Source ID [1]:
Vehicle ID [1]:
Quantity (kg) [450]:
Fat (%) [4.2]:
SNF (%) [8.6]:
Temperature (deg C) [4]:

Press ENTER to capture...

----------------------------------------------------
SIMULATED RECEPTION
----------------------------------------------------
Local ID:       1
Idempotency:    gw-blr-cc-01-simulate-cli:e6ea7d2d-e70a-48d2-9da9-3c6bcc2c51f2
Source ID:      1
Vehicle ID:     1
Scale:          450 kg
Fat:            4.2 %
SNF:            8.6 %
Temperature:    4 deg C
Local status:   outbox PENDING -> SYNCED
Cloud ID:       6
Pending sync:   0
Result: SYNCED

Note: whether the cloud accepted this as ACCEPTED or put it on HOLD is NOT visible here -
the gateway's sync contract (CloudSendResult) never returns that field, only the sync
outcome and cloud transaction id shown above. Check the frontend's Reception page for
the actual quality-validation outcome.

Capture another? (y/N):
```
(The `gatewayId`/`Idempotency` prefix above reads `gw-blr-cc-01-simulate-cli` because that was the dedicated identity used for this session's isolated verification run — see §12 item 7's full disclosure. Using the shared `gw-blr-cc-01-local` identity from §5 step 1, as this document recommends for normal use, produces the same shape of output with that identity instead.)

**Note on non-interactive/scripted use:** if you ever drive this CLI by piping a fixed block of text into its stdin (e.g. from an automated test harness), Node's `readline/promises` can race the CLI's own startup network calls and either drop buffered input or hang, because the whole piped block can arrive before the first prompt is listening. This is a generic Node/readline behavior, not a bug in this CLI, and it **does not affect normal interactive use** — a human typing at a real keyboard never triggers it, because each answer is only sent after the corresponding prompt is already displayed and being read.

### Testing duplicate/retry behavior with `pnpm simulate` (added — was previously only covered by Appendix A's separately-invoked demo script)

**Verification method for this subsection specifically:** traced directly from `src/sync/sync-engine.ts` (`tick()`/`processOne()`/`handleFailure()`), `src/sync/http-cloud-client.ts` (network-failure handling), and `src/storage/sqlite-local-storage.ts` (`findEligibleOutboxItems`/`markOutboxRetry`) — **not re-executed end-to-end in this update**, unlike the rest of this document's "actually run" claims. The mechanism below follows directly and unambiguously from that code; if you want to confirm it by execution, the outbox status after each step is directly observable via §8.

This exercises a real retry of the SAME outbox row (same `local_idempotency_key`, same Postgres `"localIdempotencyKey"`) rather than creating a second transaction — the correct way to prove "retrying does not duplicate," using only the already-documented Terminal 2/4/5 processes:

1. **Stop the API** (Terminal 2, `Ctrl+C`) before capturing.
2. In Terminal 5, run `pnpm simulate` and capture a reception as usual. `ReceptionCaptureWorkflow.captureReception()` still succeeds (it only touches SQLite) and generates the `localIdempotencyKey` **once**. The CLI's own single `syncEngine.tick()` call then attempts to reach the (stopped) API, gets a network error, and `HttpCloudClient` returns `{outcome: "retryable-error", ...}` — the CLI's final report shows `Result: PENDING (will retry - see lastError below)`.
3. Confirm the row is genuinely still pending, with the same key, via §8's query: `outbox_records.status = 'PENDING'`, `attempt_count >= 1`, `last_error` populated, and `next_attempt_at` set to a backoff-delayed timestamp (the exact backoff formula lives in `src/storage/backoff.ts`, not traced as part of this addition — the timestamp itself is what to check).
4. **Restart the API** (Terminal 2, `pnpm start:dev` again).
5. **Do not answer "Capture another?" yet — leave the CLI sitting at that prompt.** This matters: `gateway.start()` (called once, at the very top of the CLI's `main()`) unconditionally calls `syncEngine.start()` (`gateway.ts`), which arms a real 15-second `setInterval` (`sync-engine.ts`'s `start()`) — so the CLI's own gateway has a genuine background retry loop running for as long as the CLI process itself is alive (i.e. for as long as you haven't answered the prompt), on top of the one explicit `tick()` it already ran after your capture. That loop is only torn down by the `finally { ...; await gateway.stop(); }` block once you actually answer the prompt (or the process otherwise exits) — `gateway.stop()` calls `syncEngine.stop()`, which clears the interval. So the same running Terminal 5 session will retry this row on its own; you do not strictly need Terminal 4 running for this test to work, though if Terminal 4 (the long-running gateway, `pnpm start:dev`) is also running against the same `gateway.config.json`/`gateway.sqlite`, its own independent `SqliteSyncEngine` will pick up the same `PENDING` row too, whichever process's tick reaches it first.
6. Within ~15 seconds (still without answering the CLI's prompt), re-run §8's query in a separate terminal: the same `outbox_records` row should now read `status: 'SYNCED'`, and `local_transactions.cloud_transaction_id` should now be set. Only then answer "n" (or Ctrl+C) to let the CLI exit cleanly.
7. Confirm no duplicate was created in Postgres using §7's existing count query on that same `"localIdempotencyKey"` — expect exactly `1`, and expect the row's `id`/`cloudTransactionId` to be the same one now shown in SQLite.

---

## 7. VERIFY DATABASE RESULT

**These column names were read directly from the live schema this session** (`\d milk_reception_transactions`, `\d sources`, `\d vehicles` in `psql`) — TypeORM's default naming strategy preserves **camelCase**, so every multi-word column must be **double-quoted** in raw SQL (`"centreId"`, not `centre_id`). A prior revision of this document used snake_case column names that do not exist in this schema — those queries would have failed with `column "..." does not exist`; they are corrected below.

```powershell
$env:PGPASSWORD = "postgres"
$psql = "C:\Program Files\PostgreSQL\16\bin\psql.exe"

# The 5 most recent reception transactions, real verified column names:
& $psql -U postgres -h localhost -d ccmc_dev -c 'SELECT id, "transactionNumber", "centreId", "sourceId", "vehicleId", "quantityKg", fat, snf, temperature, status, "localIdempotencyKey", "receivedAt" FROM milk_reception_transactions ORDER BY id DESC LIMIT 5;'

# Look up one specific capture by its idempotency key (paste the key the CLI/demo script printed):
& $psql -U postgres -h localhost -d ccmc_dev -c "SELECT id, \"transactionNumber\", \"quantityKg\", fat, snf, temperature, status FROM milk_reception_transactions WHERE \"localIdempotencyKey\" = '<the printed key>';"

# Confirm a retry with the SAME idempotency key did not create a duplicate (expect count = 1):
& $psql -U postgres -h localhost -d ccmc_dev -c "SELECT count(*) FROM milk_reception_transactions WHERE \"localIdempotencyKey\" = '<the printed key>';"

# Sources / vehicles reference data (also camelCase — corrected from a prior revision's centre_id/vehicle_number):
& $psql -U postgres -h localhost -d ccmc_dev -c 'SELECT id, code, name, "centreId" FROM sources ORDER BY id;'
& $psql -U postgres -h localhost -d ccmc_dev -c 'SELECT id, "vehicleNumber", "centreId" FROM vehicles ORDER BY id;'
```

**Full real column list for `milk_reception_transactions`** (verified via `\d milk_reception_transactions`, so this list is exhaustive, not a guess): `id`, `"transactionNumber"`, `"centreId"`, `"sourceId"`, `"vehicleId"`, `"operatorUserId"`, `"quantityKg"`, `fat`, `snf`, `temperature`, `status`, `"readingSource"`, `reason`, `"localIdempotencyKey"`, `"receivedAt"`, `"createdAt"`, `"updatedAt"`. There is a `UNIQUE` constraint on `"localIdempotencyKey"` (`UQ_48e8713b2f5b3c9673e75615cfe`) — this is the real, database-level mechanism that makes retries with the same key impossible to duplicate, not just an application-level check.

**This exact verification was actually run this session** (Linux sandbox, not the user's Windows machine — see the final report's environment disclosure): one capture via `pnpm simulate` produced exactly one new row (`id 6`, `"localIdempotencyKey" = 'gw-blr-cc-01-simulate-cli:e6ea7d2d-...'`), and a forced retry of the same outbox row against the real API returned `outcome: "duplicate"` with the identical `cloudTransactionId`, and the row count for that key stayed at exactly `1` afterward.

---

## 8. VERIFY LOCAL GATEWAY STORAGE

`gateway.sqlite` lives at `<dataDirectory>/gateway.sqlite` (e.g. `apps\gateway\data\gateway.sqlite`). It has two tables that matter for a capture (verified via `sqlite_master` this session): **`local_transactions`** and **`outbox_records`** (plus `schema_migrations`/`gateway_metadata`, not reception-specific). Their column names are **snake_case** — this is the gateway's own SQLite schema (`src/storage/schema.js`), a completely separate naming convention from the Postgres/TypeORM schema in §7; do not mix the two up.

There is no `sqlite3.exe` dependency needed — Node's built-in `node:sqlite` (the same module the gateway itself uses) can query the file directly from PowerShell:

```powershell
cd <repo-root>\apps\gateway
node -e "const { DatabaseSync } = require('node:sqlite'); const db = new DatabaseSync('data/gateway.sqlite', { readOnly: true }); console.log('--- local_transactions ---'); console.log(db.prepare('SELECT * FROM local_transactions ORDER BY id DESC LIMIT 5').all()); console.log('--- outbox_records ---'); console.log(db.prepare('SELECT * FROM outbox_records ORDER BY id DESC LIMIT 5').all()); db.close();"
```

**Real columns, `local_transactions`:** `id`, `local_idempotency_key`, `centre_id`, `source_id`, `vehicle_id`, `quantity_kg`, `fat`, `snf`, `temperature`, `captured_at`, `created_at`, `cloud_transaction_id`.

**Real columns, `outbox_records`:** `id`, `local_transaction_id`, `local_idempotency_key`, `status`, `attempt_count`, `last_attempt_at`, `next_attempt_at`, `last_error`, `claimed_at`, `created_at`, `updated_at`.

A healthy, fully-synced capture shows `outbox_records.status = 'SYNCED'` and `local_transactions.cloud_transaction_id` set to a real integer matching the Postgres `id` from §7. This was directly verified this session (see §7's closing paragraph) — `outbox_records.id 1` read back exactly `status: 'SYNCED'`, `local_transactions.id 1` read back `cloud_transaction_id: 6`, matching the Postgres row.

---

## 9. VERIFY FRONTEND

With the frontend (§4) and API (§3) both running:
1. Log in at `http://localhost:5173/login` as `operator1@ccmc.local` / `Operator@12345` (**development-only credential** — see §4's table; never use in a real deployment).
2. Navigate to **Reception**.
3. You should see the transaction(s) created via §6/§7 — transaction numbers of the form `BLR-CC-01-<id>`, with the exact `quantityKg`/`fat`/`snf`/`temperature` you entered, and a real `status` of `ACCEPTED` or `HOLD` decided by the API's quality-validation rules (never fabricated by the gateway or this CLI — see §6's "what the CLI cannot report" note).
4. Log out, log back in as `manager1@ccmc.local` / `Manager@12345` — a `HOLD` row now shows Accept/Reject controls (Operator does not have `RECEPTION_OVERRIDE`; Manager does).
5. Navigate to **Audit** (Manager only) and confirm a `RECEPTION_CREATE` event exists for the new transaction.

**This session's verification of the frontend's data path:** the frontend's `ReceptionPage.tsx` fetches its list directly via `apiFetch<ReceptionTransactionDto[]>("/reception")` (confirmed by reading the component's source). Calling that same real endpoint (`GET /reception`) as `operator1@ccmc.local` this session returned the transaction created by `pnpm simulate` (`id: 6`) in the response body — confirming the frontend's actual data source has and will display it. A full browser-rendered check (opening the page and looking at it) was **not performed this session** — there is no browser available against this sandbox's own `localhost:5173`; do this check yourself using the steps above, or ask for it to be done via a connected browser session if one becomes available.

---

## 9a. VERIFY LOCAL DASHBOARD (works with no cloud/internet connectivity)

This is the new **edge/offline-first** operator view added this checkpoint — see `docs/gateway-architecture.md` §17 for the full architecture. Unlike §9's Reception/Dashboard pages, this page reads **only** from the Gateway's own local SQLite via its new local HTTP API — never the cloud API.

**Setup (one-time per browser):**
1. With the gateway running (§5, `localApi.enabled: true` in its config), log in to the frontend as usual (§9 step 1) and navigate to **Local Dashboard** in the nav bar (visible to anyone with `RECEPTION_VIEW`, e.g. `operator1@ccmc.local`).
2. In the form at the top, enter the local gateway URL (`http://localhost:4100` for the config sample above) and the `localApi.accessToken` value from `config/gateway.config.json` (e.g. `local-dev-only-token-blr-cc-01`). Click **Save & connect**. These are stored in this browser's `localStorage`, not sent anywhere else.

**Verify it shows real local data:**
3. Run `pnpm simulate` (§6) from `apps\gateway` and capture a reception (press ENTER at the prompt as usual).
4. Within 5 seconds (the page's poll interval), the Local Dashboard should update: **Total collection** and **Transactions today** both increase by the captured amount, and a new row appears in the **Recent transactions** table with the exact `quantityKg`/`fat`/`snf`/`temperature` you entered and a sync status badge of `PENDING` (or `SYNCED`, if the sync engine already delivered it in that window).
5. Cross-check against §8's direct SQLite query — the row shown on the dashboard must have the same `id`/`local_idempotency_key` as the newest row in `local_transactions`. There is no ACCEPTED/HOLD/REJECTED shown here (see the on-page note) — that is decided by the cloud and is not part of this local schema (§17b); only sync status is shown.

**Verify it survives cloud/network being unreachable — the actual offline test:**
6. Stop the API (Ctrl+C in its terminal, §3), or otherwise make `cloudApiBaseUrl` unreachable (e.g. temporarily edit `gateway.config.json` to point at a bad port and restart the gateway).
7. With the gateway itself still running, run `pnpm simulate` again and capture another reception.
8. Confirm via §8's SQLite query that the new row exists in `local_transactions` and its `outbox_records` row is `PENDING` (a real sync attempt could not succeed with the cloud down).
9. Confirm the Local Dashboard **still shows this new transaction** (within one poll interval) — Total collection/Transactions today increase, the new row appears with a `PENDING` badge. The dashboard's "Connected to local gateway" banner should remain green throughout (it reflects reachability of the **gateway**, not the cloud) — `cloudConnectivity` in that banner may still read `CONNECTED` if a prior sync already succeeded this run (§5's health-field semantics: it is sticky and never flips back to a negative state), so the authoritative offline signal is the outbox status turning/staying `PENDING`, not this banner text.
10. Restart the API (§3) / restore the real `cloudApiBaseUrl` and restart the gateway. Within the sync engine's normal retry window (§7 backoff), confirm via §7's Postgres query that the transaction now has a real cloud `id`, and via §8 that its local `outbox_records.status` has flipped to `SYNCED` and `local_transactions.cloud_transaction_id` is now set — and that exactly one Postgres row exists for it (no duplicate), the same idempotency guarantee §7 already verifies for the online case.

---

## 10. DEPLOYMENT PACKAGING

**Exact command:**
```powershell
cd <repo-root>\apps\gateway
pnpm package:deployment
```
(equivalent to `node packaging/build-deployment.mjs [outputDir]`; `outputDir` defaults to `apps\gateway\dist-deployment`.)

### The `spawnSync npx ENOENT` fix
**Root cause:** on Windows, `npx` is installed as `npx.cmd`, a shell shim — not a bare `.exe`. `child_process.execFileSync("npx", [...])` (as the script previously did, without `shell: true`) resolves the executable name directly, like `CreateProcess`, **not** through `cmd.exe`'s `PATHEXT`-based lookup. So it fails to find a bare, extension-less `"npx"` and throws `ENOENT` — even though `npx` works fine typed interactively at a real PowerShell prompt. This is a well-documented Node-on-Windows gotcha, not specific to this repository.

**Fix applied** (`apps/gateway/packaging/build-deployment.mjs`): replaced the `npx tsc` invocation with Node's own module resolution plus a direct interpreter call:
```js
const require = createRequire(import.meta.url);
const tscBin = require.resolve("typescript/bin/tsc"); // typescript is already a real devDependency
execFileSync(process.execPath, [tscBin, "-p", path.join(gatewayRoot, "tsconfig.json")], {
  cwd: gatewayRoot,
  stdio: "inherit",
});
```
This avoids PATH/`PATHEXT`/shell resolution entirely — it is "load this specific file with this specific interpreter" (`process.execPath`, the exact same `node.exe` already running the script), which resolves identically on Windows, macOS, and Linux, and introduces **no new dependency** (`typescript` was already a devDependency). A `require.resolve` failure now throws a clear, actionable error message (naming `pnpm install`, and separately noting that a broken/unreadable `node_modules/typescript` symlink — as can happen in some sandboxed file-mount environments — would not be fixed by reinstalling).

**Validation status — stated exactly, per environment:**
- **This session's Linux sandbox:** fully validated. `node packaging/build-deployment.mjs` and the exact `pnpm package:deployment` entrypoint both completed successfully (after also resolving a separate, pre-existing `pnpm-workspace.yaml` build-approval gap — see below); the 5-assertion regression suite (`test/packaging/build-deployment.spec.ts`) passed 5/5.
- **The user's real Windows machine, via a remote device-bridge shell:** attempted, but that bridge shell runs inside its own Linux VM that cannot properly read through this monorepo's pnpm-symlinked `node_modules` on the mounted Windows drive (`readlink node_modules/typescript` returns empty/I-O error there, for reasons unrelated to this fix) — this is a bridge-specific limitation, not evidence the fix is broken, and it is **not** the same thing as running in the user's actual Windows PowerShell.
- **The user's actual Windows PowerShell, run directly by the user:** **not executed by this assistant** — no direct access to that shell exists. This is the one environment that matters most for closing out the originally-reported bug, and it has not yet been confirmed. Please run `pnpm package:deployment` yourself from `apps\gateway` in a real PowerShell window and report back whether `spawnSync npx ENOENT` still occurs.

### A separate, pre-existing `pnpm install`/`pnpm-workspace.yaml` issue found while validating this
Independent of the `npx` fix, a fresh `pnpm install` in this environment failed with:
```
[ERR_PNPM_IGNORED_BUILDS] Ignored build scripts: @nestjs/core@..., bcrypt@..., esbuild@...
Run "pnpm approve-builds" to pick which dependencies should be allowed to run scripts.
```
This is a real, pre-existing gap in `pnpm-workspace.yaml`: it already listed `onlyBuiltDependencies: [@nestjs/core, bcrypt, esbuild]`, but had no matching `allowBuilds` approval, which newer pnpm versions require before running any dependency's install/postinstall script. Fixed by running pnpm's own standard approval command once:
```powershell
pnpm approve-builds --all
```
This persists the approval into `pnpm-workspace.yaml` (adds a small `allowBuilds: {...: true}` block) so it is a one-time, per-machine step, not something to repeat on every install. This is unrelated to the `npx`/`tsc` fix above but blocks `pnpm install` (and therefore `pnpm package:deployment`, which reinstalls dependencies as part of its own `pnpm` invocation) on a machine that hasn't approved these builds yet — documented here and in §12 Troubleshooting since it is a real step a fresh checkout may need.

### What the generated package actually contains — real vs. placeholder, stated explicitly
```
dist-deployment\
  CCMCGateway.exe            <- PLACEHOLDER TEXT FILE, not a real .exe
  CCMCGateway.xml             <- real WinSW config, copied verbatim
  node\node.exe               <- PLACEHOLDER TEXT FILE, not a real .exe
  app\dist\main.js             <- real, produced by an actual tsc build this run
  app\node_modules\README.txt  <- real note (gateway declares zero runtime deps today)
  config\gateway.config.example.json  <- real, copied verbatim
  data\.gitkeep, logs\.gitkeep          <- real placeholders for empty dirs
  install.ps1 / uninstall.ps1          <- real PowerShell, copied verbatim
```
**Do not treat this generated package as production-installable.** `CCMCGateway.exe` (the WinSW v3 executable) and `node\node.exe` (a vendored Windows Node build) are deliberately-labeled placeholder text files — this sandbox's network egress returns `403 Forbidden` against both `github.com/winsw/winsw/releases` and `nodejs.org/dist/`, so it cannot fetch the real binaries, and the script says so in its own header comment and in each placeholder file's contents. Everything else listed above as "real" genuinely is (verified by the regression test and by direct inspection of the output this session). Producing the real two binaries is a Checkpoint 6/11-tracked follow-up task requiring either an unrestricted-egress build environment or doing that one step by hand on a real Windows machine at actual packaging time — it was not silently glossed over, and this was already the documented decision before this session; nothing about that decision was changed.

---

## 11. WINDOWS SERVICE

**Not verified on this machine.** This sandbox is Linux-only; there is no way to test Windows SCM (Service Control Manager) behavior — installation, start/stop, reboot-autostart, crash-restart, or uninstall — from here, and none of that has been done this session or in any prior session of this project.

What genuinely exists and was reviewed (not run): a real, schema-reviewed `service/CCMCGateway.xml` (WinSW v3 config; service id `CCMCGateway`, executable `node\node.exe app\dist\main.js`, working directory `%BASE%`, `<startmode>Automatic</startmode>`, `<onfailure action="restart" delay="5 sec"/>`, `<resetfailure>1 day</resetfailure>`, logs rolled at 10 MB × 8 files), plus real PowerShell `service/install.ps1` (verifies required files exist, creates `data\`/`logs\` if absent, requires an elevated PowerShell session, runs `CCMCGateway.exe install` then `start`) and `service/uninstall.ps1` (stops then uninstalls; does not delete `data\`/`config\`/`logs\`).

**You cannot actually install this as a running Windows service using only what's in this repository as-is** — §10 already explained that `CCMCGateway.exe` and `node\node.exe` are placeholder text files in the packaged output, not real executables. Running `install.ps1` against them will fail at the "these are not real binaries" point. Genuine service testing requires substituting real WinSW v3 and Windows Node.exe binaries for the two placeholders first — this exact substitution and the subsequent install/start/reboot/crash-restart/uninstall sequence has never been executed on a real Windows machine at any point in this project's history.

---

## 12. TROUBLESHOOTING

1. **SYMPTOM:** `pnpm install` fails with `[ERR_PNPM_IGNORED_BUILDS] Ignored build scripts: ...`.
   → **Cause:** pnpm requires explicit per-package build-script approval; this repo's `pnpm-workspace.yaml` had `onlyBuiltDependencies` but no matching `allowBuilds` (see §10).
   → **Fix:** `pnpm approve-builds --all`, then re-run `pnpm install`. One-time per machine.

2. **SYMPTOM:** `pnpm package:deployment` (or `node packaging/build-deployment.mjs`) fails with `spawnSync npx ENOENT`.
   → **Cause:** this was the originally-reported bug — see §10 for the full root cause and fix.
   → **Verify the fix is present:** `apps\gateway\packaging\build-deployment.mjs` should invoke `execFileSync(process.execPath, [tscBin, ...])`, not `execFileSync("npx", ...)`.
   → **If it still fails after confirming the fix is present:** check the exact error — a `Cannot find module 'typescript/bin/tsc'` message (rather than `ENOENT`) means `typescript` isn't actually installed in `apps/gateway/node_modules` (run `pnpm install` from the repo root) or, in rare sandboxed/mounted-filesystem setups, that `node_modules/typescript` is an unreadable symlink (reinstalling won't help there — run the script against a real, local filesystem).

3. **SYMPTOM:** `psql`/`createdb` "is not recognized as the name of a cmdlet...".
   → **Cause:** PostgreSQL's `bin` directory isn't on PATH.
   → **Verify:** `Get-Command psql -ErrorAction SilentlyContinue` returns nothing.
   → **Fix:** use the full path (§1 item 4), or add `C:\Program Files\PostgreSQL\16\bin` to PATH for the session.

4. **SYMPTOM:** API fails to start with a database connection error (`ECONNREFUSED` or similar from `pg`).
   → **Cause:** PostgreSQL isn't running, or `DATABASE_URL` in `apps\api\.env` doesn't match your actual instance.
   → **Verify:** `Get-Service -Name "postgresql*"`; then `& psql -U postgres -h localhost -d ccmc_dev -c "SELECT 1;"`.
   → **Fix:** start the Postgres service, or edit `apps\api\.env`'s `DATABASE_URL`.

5. **SYMPTOM:** API starts but `pnpm migration:run` says the schema is already there, or the frontend shows empty lists everywhere.
   → **Cause:** migrations ran but `pnpm seed` never did (or vice versa).
   → **Verify:** `psql -d ccmc_dev -c "\dt"` (tables present?); `psql -d ccmc_dev -c "SELECT count(*) FROM users;"` (0 or 6?).
   → **Fix:** `cd apps\api; pnpm migration:run; pnpm seed`.

6. **SYMPTOM:** frontend shows a spinner forever or a network error on login; devtools shows `ECONNREFUSED`/`502` on `/api/*` calls.
   → **Cause:** the API isn't running, or Vite's hardcoded proxy target (`apps\web\vite.config.ts`) doesn't match where the API actually listens.
   → **Verify:** `Invoke-RestMethod http://localhost:3000/auth/login -Method Post -ContentType "application/json" -Body '{"email":"operator1@ccmc.local","password":"Operator@12345"}'` directly.
   → **Fix:** start the API (§3); if you changed `PORT` in `apps\api\.env`, also edit `apps\web\vite.config.ts`'s proxy target (it is not environment-driven).

7. **SYMPTOM:** gateway/`pnpm simulate` fails with `"Gateway configuration error: Gateway config file not found at ..."`.
   → **Cause:** `config\gateway.config.json` doesn't exist yet.
   → **Verify:** `Test-Path apps\gateway\config\gateway.config.json`.
   → **Fix:** `copy config\gateway.config.example.json config\gateway.config.json` then edit it (§5 step 1).

8. **SYMPTOM:** gateway/`pnpm simulate` fails with `"Missing or invalid required config field ..."`.
   → **Cause:** one of the seven required fields is missing, empty, or the wrong type (`centreId` as a quoted string instead of a bare integer is the most common mistake).
   → **Fix:** correct the field per §5 step 1's exact JSON.

9. **SYMPTOM:** `pnpm simulate` (or the sync engine generally) fails during sync with a `retryable-error`/`terminal-error` outcome.
   → **Cause:** the API isn't running, `cloudApiBaseUrl` in `gateway.config.json` doesn't match where it's actually listening, or `cloudAuthEmail`/`cloudAuthPassword` don't match a real seeded `GatewayService` account.
   → **Verify:** `Invoke-RestMethod http://localhost:3000/auth/login -Method Post -ContentType "application/json" -Body '{"email":"gateway-blr-cc-01@ccmc.local","password":"GatewayBLR@2026!sync"}'` — does the gateway credential itself authenticate?
   → **Fix:** start the API; correct `gateway.config.json` to match §2/§5's seeded values exactly.

10. **SYMPTOM:** gateway throws `GatewayIdentityMismatchError` on start (long-running or `pnpm simulate`).
    → **Cause:** `gateway.sqlite` already exists from a previous run and was pinned to a different `gatewayId`/`centreId` than what's currently in `gateway.config.json`.
    → **Verify:** the error message states both the pinned and currently-configured values.
    → **Fix:** either restore `gateway.config.json`'s original values, or point `dataDirectory` at a fresh, empty directory for a genuinely new identity.

11. **SYMPTOM:** port `3000` or `5173` "already in use".
    → **Cause:** a previous `pnpm start:dev`/`pnpm dev` process is still running.
    → **Verify:** `Get-NetTCPConnection -LocalPort 3000` / `-LocalPort 5173`, then `Get-Process -Id <pid>`.
    → **Fix:** stop that process, or change `PORT` in `apps\api\.env` (and update `apps\web\vite.config.ts`'s proxy target to match).

12. **SYMPTOM:** driving `pnpm simulate` non-interactively (piped/scripted stdin) hangs or silently drops answers.
    → **Cause:** a generic Node `readline/promises` behavior — piped input can arrive before the first prompt is listening (see §6's closing note). Not a bug in the CLI, and irrelevant to normal interactive keyboard use.
    → **Fix (only needed for scripted/automated driving, e.g. a test harness):** drive it over a real pseudo-terminal and only send each answer after its corresponding prompt text has actually appeared in the child's output, rather than piping a fixed block of text all at once.

---

## 13. CURRENT LIMITATIONS

- **No real RS232 device integration** — not implemented anywhere in this repository. No serial-port library, no baud rate, no packet parser exists for any real scale/analyser. Blocked entirely on manufacturer/protocol specifications that have not yet been supplied.
- **No real Bluetooth device integration** — same as above; not implemented, no protocol assumed.
- **No manufacturer-specific protocols** — none implemented or guessed at; the `Transport`/`Parser` abstraction exists in the type system as a seam for this future work, but nothing concrete sits behind it. `WeighingScaleSimulator`/`MilkAnalyserSimulator` are the only implementations of the device interfaces that exist, and both are used purely for hardware-independent development/testing (via `simulate-e2e-demo.ts` and now `pnpm simulate`, §6) until real hardware specs arrive.
- **Does the Simulator Capture CLI (`pnpm simulate`) talk to an already-running long-lived gateway process?** No — see §6's Architecture section. It runs the real pipeline **in-process** (Option A), sharing the same `gateway.sqlite` file as any long-running gateway pointed at the same config, but there is no IPC/HTTP connection between the two processes; none was invented.
- **`deviceConnectivity` in the health snapshot** — permanently `"UNKNOWN"`; no code path anywhere ever calls `setDeviceConnectivity(...)`, simulators or not.
- **`cloudConnectivity`'s `DISCONNECTED` state** — never reported; only `UNKNOWN`→`CONNECTED` (one-way) is wired, deliberately, because the current error shape can't safely distinguish "network down" from "cloud returned an error" without guessing.
- **Windows service installation has not been tested** — see §11. The WinSW XML and PowerShell scripts are real and reviewed, but two of the binaries the packaged output depends on are placeholders (§10), and the full install→start→reboot→crash-restart→uninstall sequence has never been executed on an actual Windows machine.
- **The `spawnSync npx ENOENT` fix (§10) has been validated in a Linux sandbox and blocked-but-not-refuted in a Windows-hosted Linux bridge shell, but has not yet been confirmed by actually running `pnpm package:deployment` in the user's own real Windows PowerShell.** This is the one remaining piece of proof needed to fully close out the original bug report.

---

## Appendix A — the pre-existing `simulate-e2e-demo.ts` script (kept, unaltered, superseded for interactive use by §6)

This is the exact content of the previous revision of this document's §6/§10 sections, kept here verbatim so no existing, working instructions are lost. `simulate-e2e-demo.ts` still exists in the repository unmodified and still works exactly as described.

### How simulator-backed capture worked before `pnpm simulate` existed

There is no CLI command, API endpoint, or UI to trigger a simulated capture against the long-running gateway process — `main.ts` has exactly two modes (default long-running, and `--status`/`--version` diagnostic), neither of which constructs a simulator or runs a capture.

What exists and genuinely exercises the real pipeline is `apps/gateway/scripts/simulate-e2e-demo.ts`, added specifically to prove the real simulator → SQLite → outbox → sync → cloud path end to end. It is excluded from the compiled build (`tsconfig.json`'s `include` doesn't cover `scripts/`) and constructs its own `Gateway`/simulators/`ReceptionCaptureWorkflow` directly in-process, just like `pnpm simulate` does — the difference is that it is a fixed, non-interactive, env-var-driven proof script with one hardcoded scenario (a valid capture, an idempotent retry, and an out-of-range HOLD capture), not something an operator types fields into.

```powershell
cd <repo-root>\apps\gateway
$env:E2E_CLOUD_API_BASE_URL = "http://localhost:3000"
$env:E2E_GATEWAY_AUTH_EMAIL = "gateway-blr-cc-01@ccmc.local"
$env:E2E_GATEWAY_AUTH_PASSWORD = "GatewayBLR@2026!sync"
$env:E2E_CENTRE_ID = "1"
$env:E2E_SOURCE_ID = "1"
$env:E2E_VEHICLE_ID = "1"
npx ts-node scripts\simulate-e2e-demo.ts
```

Expected sections in its output: `SETUP`, `6D.2 - SIMULATOR CAPTURE (ACCEPTED case)`, `6D.3 - SYNC`, `6D.4 - IDEMPOTENCY`, a second capture below the seeded FAT minimum to prove a real server-determined `HOLD`, and `6D.6 - FINAL GATEWAY STATUS`. Each section's own assertions throw if the expected outcome isn't reached, so a clean run is real signal, not just absence of a crash.

**Troubleshooting specific to this script:** `"Missing required environment variable E2E_..."` means one of the six `$env:E2E_*` lines above wasn't set in the current PowerShell session (they don't persist across terminals) — re-set all six in the exact terminal you run it from.
