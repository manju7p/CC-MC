/**
 * CC-MC Gateway Simulator CLI (development/test tool, not shipped in the
 * production build - see tsconfig.json's `include: ["src/**\/*.ts"]`,
 * which excludes everything under scripts/, exactly like
 * scripts/simulate-e2e-demo.ts).
 *
 * Purpose: give an operator/developer a single interactive command to
 * drive one real, end-to-end simulated milk reception through the ACTUAL
 * gateway pipeline, without needing real scale/analyser hardware (still
 * blocked on manufacturer/protocol specs - see docs/gateway-architecture.md)
 * and without hand-writing a one-off script each time (what
 * simulate-e2e-demo.ts already is - a fixed, non-interactive proof script,
 * not an operator tool). This CLI is the missing "operator trigger" that
 * gap analysis found: the long-running gateway (`pnpm start`) never
 * registers any simulator and has no CLI/API/UI to fire a capture (see
 * main.ts - its only two modes are the long-running service and the
 * --status/--version diagnostic). This script is additive: it does not
 * change main.ts, does not add an HTTP endpoint, and does not touch
 * apps/web.
 *
 * ARCHITECTURE (read this before assuming otherwise): there is no IPC or
 * HTTP mechanism anywhere in this repository for a separate process to
 * talk to an already-running `pnpm start` gateway - main.ts exposes
 * nothing to attach to. This CLI therefore runs "Option A": it
 * constructs its OWN Gateway/ReceptionCaptureWorkflow/SyncEngine
 * in-process, using the exact same gateway.config.json (via
 * loadConfig/resolveConfigPath - the identical config the long-running
 * gateway and --status use) and therefore the exact same gateway.sqlite
 * file. That shared file is what makes this cooperative rather than a
 * fake parallel data path: if a real `pnpm start` gateway happens to be
 * running concurrently against the same config, this CLI's capture is a
 * genuine new row in the SAME outbox that process's own SyncEngine polls
 * every 15s - no invented IPC needed for that cooperation, because
 * outbox state lives in a real shared SQLite file, not in either
 * process's memory. This CLI does not rely on that coincidence, though:
 * it drives its own SqliteSyncEngine.tick() once, immediately, so a
 * single run is a complete, self-contained proof with an immediate
 * result, whether or not another gateway process happens to be running.
 *
 * Concurrency safety: like main.ts's --status mode, this CLI's
 * storage.init() is called with { recoverStaleProcessing: false } - it
 * may run at the same moment as an already-running long-lived gateway
 * service instance against the same gateway.sqlite, and the unconditional
 * "every PROCESSING row is orphaned" sweep assumption is only true for a
 * genuinely fresh long-running instance, not a second, short-lived
 * process opening the same file (see storage.types.ts's init() doc
 * comment - the exact hazard Checkpoint 6B already found and fixed for
 * --status; this CLI is the same kind of caller).
 *
 * DOMAIN MODEL - every prompted field below is a REAL field:
 *   - centreId: NOT prompted - taken from gateway.config.json, exactly
 *     like the real gateway. A gateway serves exactly one centre; this
 *     CLI does not invent a "switch centre" capability the real
 *     architecture doesn't have.
 *   - sourceId / vehicleId: real, required fields of
 *     CreateReceptionRequest (packages/shared-types/src/index.ts) /
 *     CreateReceptionDto (apps/api/src/reception/dto/create-reception.dto.ts).
 *     This CLI CANNOT look these up via the cloud API the way a human
 *     operator's frontend session can: GET /sources and GET /vehicles
 *     both require SOURCE_VIEW/VEHICLE_VIEW (see those controllers),
 *     permissions the GatewayService role deliberately does NOT hold
 *     (see apps/api/src/seed.ts - it holds RECEPTION_CREATE only). This
 *     is a real, load-bearing RBAC fact, not an oversight - a gateway
 *     credential that could read source/vehicle master data would be a
 *     wider blast radius than "create receptions for my one centre."
 *     So the only available defaults are the ones already seeded
 *     (apps/api/src/seed.ts: SRC-BLR-001/id=1 for centre 1, KA01AB1234/id=1
 *     for centre 1) - shown as a labeled default, always overridable by
 *     typing a different id.
 *   - quantityKg / fat / snf / temperature: the REAL fields the scale and
 *     analyser simulators produce (ScaleReading.weightKg,
 *     AnalyserReading.fat/snf/temperature - see
 *     normalization/normalized-reading.types.ts) and the REAL fields
 *     CreateReceptionRequest requires. There is no "Volume (L)" field
 *     anywhere in this system - the domain model is mass in kilograms
 *     (quantityKg), not a volume unit, so this CLI prompts for
 *     "Quantity (kg)", not liters.
 *
 * WHAT THIS CLI CANNOT REPORT, AND WHY: the cloud's ACCEPTED/HOLD
 * decision is never returned to the gateway. CloudSendResult
 * (sync/cloud-client.types.ts) only carries `outcome`
 * ("created"/"duplicate"/"retryable-error"/"terminal-error") and
 * `cloudTransactionId` - there is no status field in that contract, by
 * design (the gateway's HttpCloudClient calls POST /reception and never
 * calls GET /reception/:id, which would need RECEPTION_VIEW - another
 * permission GatewayService does not hold). So this CLI's final report
 * shows the real sync outcome and cloud transaction id, but explicitly
 * does NOT claim an ACCEPTED/HOLD status - it says to check the frontend
 * for that, rather than inventing a field that does not exist in the
 * actual data flow back to the gateway.
 */
import * as readline from "node:readline/promises";
import { loadConfig, resolveConfigPath, ConfigValidationError } from "../src/config/config.loader";
import { Logger } from "../src/logging/logger";
import { SqliteLocalStorage } from "../src/storage/sqlite-local-storage";
import { HttpCloudClient } from "../src/sync/http-cloud-client";
import { SqliteSyncEngine } from "../src/sync/sync-engine";
import { Gateway } from "../src/gateway";
import { WeighingScaleSimulator } from "../src/device/simulators/weighing-scale-simulator";
import { MilkAnalyserSimulator } from "../src/device/simulators/milk-analyser-simulator";
import { ReceptionCaptureWorkflow } from "../src/capture/reception-capture-workflow";

const DIVIDER = "-".repeat(52);

function section(title: string): void {
  process.stdout.write(`\n${DIVIDER}\n${title}\n${DIVIDER}\n`);
}

/**
 * Best-effort friendly centre name for the header only - GET /centres
 * requires no permission beyond authentication (see
 * apps/api/src/centres/centres.controller.ts: @UseGuards(JwtAuthGuard),
 * no @RequirePermission), so the gateway's own service-account credential
 * IS genuinely allowed to call this one real endpoint, unlike
 * /sources or /vehicles. If this fails for any reason (API not running,
 * network issue), the CLI falls back to showing the numeric centreId
 * alone rather than blocking the whole tool on a display nicety.
 */
async function tryFetchCentreName(
  baseUrl: string,
  email: string,
  password: string,
  centreId: number,
): Promise<string | null> {
  try {
    const loginRes = await fetch(`${baseUrl}/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password }),
    });
    if (!loginRes.ok) return null;
    const loginBody = (await loginRes.json()) as { accessToken: string };

    const centresRes = await fetch(`${baseUrl}/centres`, {
      headers: { Authorization: `Bearer ${loginBody.accessToken}` },
    });
    if (!centresRes.ok) return null;
    const centres = (await centresRes.json()) as Array<{ id: number; name: string; code: string }>;
    const match = centres.find((c) => c.id === centreId);
    return match ? `${match.name} (${match.code})` : null;
  } catch {
    return null;
  }
}

async function promptNumber(rl: readline.Interface, label: string, defaultValue: number): Promise<number> {
  const answer = (await rl.question(`${label} [${defaultValue}]: `)).trim();
  if (answer === "") return defaultValue;
  const parsed = Number(answer);
  if (!Number.isFinite(parsed)) {
    process.stdout.write(`  Not a number, using default ${defaultValue}.\n`);
    return defaultValue;
  }
  return parsed;
}

async function main(): Promise<void> {
  const configPath = resolveConfigPath();
  let config;
  try {
    config = loadConfig(configPath);
  } catch (err) {
    if (err instanceof ConfigValidationError) {
      process.stderr.write(`Gateway configuration error: ${err.message}\n`);
      process.exitCode = 1;
      return;
    }
    throw err;
  }

  const logger = new Logger("simulate-capture-cli");
  const storage = new SqliteLocalStorage(config, logger.child("storage"));
  const cloudClient = new HttpCloudClient(
    config.cloudApiBaseUrl,
    config.cloudAuthEmail,
    config.cloudAuthPassword,
    logger.child("cloud-client"),
  );
  const syncEngine = new SqliteSyncEngine(storage, cloudClient, logger.child("sync-engine"));
  const gateway = new Gateway(config, "simulate-capture-cli", logger, storage, syncEngine);

  // See this file's header comment: this CLI may run concurrently with an
  // already-running long-lived gateway service against the same
  // gateway.sqlite, so it opts out of the startup stale-PROCESSING sweep,
  // exactly like main.ts's --status/--version mode does.
  await gateway.start({ recoverStaleProcessing: false });

  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });

  try {
    section("CC-MC Gateway Simulator");

    const centreName = await tryFetchCentreName(
      config.cloudApiBaseUrl,
      config.cloudAuthEmail,
      config.cloudAuthPassword,
      config.centreId,
    );
    process.stdout.write(`Centre:     ${centreName ?? `(centre id ${config.centreId})`}\n`);
    process.stdout.write(`Gateway ID: ${config.gatewayId}\n`);
    process.stdout.write(`Cloud API:  ${config.cloudApiBaseUrl}\n`);

    let again = true;
    while (again) {
      process.stdout.write("\nEnter reception details (press Enter to accept the default shown):\n\n");

      // Defaults are the seeded reference data for centre 1
      // (apps/api/src/seed.ts: SRC-BLR-001/id=1, KA01AB1234/id=1) - the
      // ONLY defaults this CLI has any basis for, since it cannot query
      // GET /sources or GET /vehicles (see header comment on RBAC).
      const sourceId = await promptNumber(rl, "Source ID", 1);
      const vehicleId = await promptNumber(rl, "Vehicle ID", 1);
      const quantityKg = await promptNumber(rl, "Quantity (kg)", 450);
      const fat = await promptNumber(rl, "Fat (%)", 4.2);
      const snf = await promptNumber(rl, "SNF (%)", 8.6);
      const temperature = await promptNumber(rl, "Temperature (deg C)", 4.0);

      await rl.question("\nPress ENTER to capture...");

      const now = new Date().toISOString();
      const scale = new WeighingScaleSimulator(`${config.gatewayId}-sim-scale`);
      const analyser = new MilkAnalyserSimulator(`${config.gatewayId}-sim-analyser`);
      await scale.connect();
      await analyser.connect();
      scale.setNextReadings([{ weightKg: quantityKg, stable: true, capturedAt: now }]);
      analyser.setNextReadings([{ fat, snf, temperature, capturedAt: now }]);

      const workflow = new ReceptionCaptureWorkflow(scale, analyser, storage, config.gatewayId);

      let result;
      try {
        result = await workflow.captureReception({ centreId: config.centreId, sourceId, vehicleId });
      } catch (err) {
        process.stderr.write(`\nCapture failed: ${(err as Error).message}\n`);
        await scale.disconnect();
        await analyser.disconnect();
        again = (await rl.question("\nCapture another? (y/N): ")).trim().toLowerCase() === "y";
        continue;
      }
      await scale.disconnect();
      await analyser.disconnect();

      const { localTransaction, outboxRecord } = result;

      // Real sync, driven directly (same public tick() method the
      // long-running gateway's 15s poll loop calls internally - see
      // sync-engine.ts's doc comment) rather than waiting out the interval.
      await syncEngine.tick();

      const finalOutbox = await storage.getOutboxRecordById(outboxRecord.id);
      const finalTransaction = await storage.getLocalTransactionById(localTransaction.id);
      const health = await gateway.getHealthSnapshot();

      section("SIMULATED RECEPTION");
      process.stdout.write(`Local ID:       ${localTransaction.id}\n`);
      process.stdout.write(`Idempotency:    ${localTransaction.localIdempotencyKey}\n`);
      process.stdout.write(`Source ID:      ${sourceId}\n`);
      process.stdout.write(`Vehicle ID:     ${vehicleId}\n`);
      process.stdout.write(`Scale:          ${quantityKg} kg\n`);
      process.stdout.write(`Fat:            ${fat} %\n`);
      process.stdout.write(`SNF:            ${snf} %\n`);
      process.stdout.write(`Temperature:    ${temperature} deg C\n`);
      process.stdout.write(`Local status:   outbox ${outboxRecord.status} -> ${finalOutbox?.status ?? "unknown"}\n`);
      process.stdout.write(`Cloud ID:       ${finalTransaction?.cloudTransactionId ?? "(not yet synced)"}\n`);
      process.stdout.write(`Pending sync:   ${health.pendingSyncCount}\n`);

      if (finalOutbox?.status === "SYNCED") {
        process.stdout.write(`Result: SYNCED\n`);
      } else if (finalOutbox?.status === "FAILED") {
        process.stdout.write(`Result: FAILED (${finalOutbox.lastError ?? "see logs"})\n`);
      } else {
        process.stdout.write(`Result: PENDING (will retry - see lastError below)\n`);
        if (finalOutbox?.lastError) process.stdout.write(`Last error: ${finalOutbox.lastError}\n`);
      }
      process.stdout.write(
        `\nNote: whether the cloud accepted this as ACCEPTED or put it on HOLD is NOT visible here -\n` +
          `the gateway's sync contract (CloudSendResult) never returns that field, only the sync\n` +
          `outcome and cloud transaction id shown above (see this file's header comment). Check the\n` +
          `frontend's Reception page for the actual quality-validation outcome.\n`,
      );

      again = (await rl.question("\nCapture another? (y/N): ")).trim().toLowerCase() === "y";
    }
  } finally {
    rl.close();
    await gateway.stop();
  }
}

main().catch((err) => {
  process.stderr.write(`\nFatal error: ${(err as Error).stack ?? (err as Error).message}\n`);
  process.exitCode = 1;
});
