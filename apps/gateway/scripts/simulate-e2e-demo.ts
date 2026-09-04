/**
 * Checkpoint 6D: the smallest real orchestration mechanism needed to
 * exercise the ENTIRE existing pipeline end to end -
 *
 *   simulated scale + simulated analyser
 *     -> ReceptionCaptureWorkflow (real)
 *     -> localIdempotencyKey (real, generated once)
 *     -> SqliteLocalStorage (real, on-disk gateway.sqlite)
 *     -> outbox record (real)
 *     -> SqliteSyncEngine.tick() (real)
 *     -> HttpCloudClient (real, real fetch, real JWT login)
 *     -> the actual running NestJS API
 *     -> the actual running PostgreSQL database
 *
 * against a REAL, already-running NestJS API + PostgreSQL - no mocked
 * cloud, no fake endpoint, no bypassed layer. Every class used here is the
 * exact production class from src/ - this script only supplies the
 * orchestration (construct, call in order, print evidence) that no
 * existing CLI/entry point currently provides, per the explicit
 * instruction: "If an orchestration/CLI/test harness is missing,
 * implement the SMALLEST real orchestration mechanism needed to exercise
 * the existing pipeline. Do not create an alternative architecture."
 *
 * NOT a test file (no jest, no mocks) and NOT shipped in the production
 * build (tsconfig.json's rootDir is ./src, so this file, living under
 * scripts/, is never compiled into dist/ or packaged - see
 * packaging/build-deployment.mjs, which only copies dist/). Run directly
 * via ts-node against a live API for a one-off, human-verifiable proof.
 *
 * Requires environment variables (no defaults/guessing - see README below
 * printed if any are missing):
 *   E2E_CLOUD_API_BASE_URL   e.g. http://localhost:3000
 *   E2E_GATEWAY_AUTH_EMAIL   a real seeded GatewayService account email
 *   E2E_GATEWAY_AUTH_PASSWORD
 *   E2E_CENTRE_ID            a real, seeded chilling centre id
 *   E2E_SOURCE_ID            a real source id belonging to that centre
 *   E2E_VEHICLE_ID           a real vehicle id belonging to that centre
 *   E2E_DATA_DIR             (optional) where gateway.sqlite is created -
 *                            defaults to a fresh temp directory so re-runs
 *                            never collide with a previous run's identity.
 */
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { Logger } from "../src/logging/logger";
import { SqliteLocalStorage } from "../src/storage/sqlite-local-storage";
import { HttpCloudClient } from "../src/sync/http-cloud-client";
import { SqliteSyncEngine } from "../src/sync/sync-engine";
import { Gateway } from "../src/gateway";
import { WeighingScaleSimulator } from "../src/device/simulators/weighing-scale-simulator";
import { MilkAnalyserSimulator } from "../src/device/simulators/milk-analyser-simulator";
import { ReceptionCaptureWorkflow } from "../src/capture/reception-capture-workflow";
import type { GatewayConfig } from "../src/config/config.types";

function requireEnv(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(
      `Missing required environment variable ${name}. This script refuses to guess a centre/source/vehicle/credential - ` +
        `pass real, already-seeded values. See this file's header comment for the full list.`,
    );
  }
  return value;
}

function section(title: string): void {
  process.stdout.write(`\n${"=".repeat(78)}\n${title}\n${"=".repeat(78)}\n`);
}

async function main() {
  const cloudApiBaseUrl = requireEnv("E2E_CLOUD_API_BASE_URL");
  const cloudAuthEmail = requireEnv("E2E_GATEWAY_AUTH_EMAIL");
  const cloudAuthPassword = requireEnv("E2E_GATEWAY_AUTH_PASSWORD");
  const centreId = parseInt(requireEnv("E2E_CENTRE_ID"), 10);
  const sourceId = parseInt(requireEnv("E2E_SOURCE_ID"), 10);
  const vehicleId = parseInt(requireEnv("E2E_VEHICLE_ID"), 10);
  const dataDirectory = process.env.E2E_DATA_DIR ?? fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-e2e-demo-"));
  fs.mkdirSync(dataDirectory, { recursive: true });

  const gatewayId = `gw-e2e-demo-${Date.now()}`;
  const config: GatewayConfig = {
    gatewayId,
    centreId,
    cloudApiBaseUrl,
    logDirectory: path.join(dataDirectory, "logs"),
    dataDirectory,
    cloudAuthEmail,
    cloudAuthPassword,
  };

  section("SETUP");
  process.stdout.write(`gatewayId:      ${gatewayId}\n`);
  process.stdout.write(`centreId:       ${centreId}\n`);
  process.stdout.write(`sourceId:       ${sourceId}\n`);
  process.stdout.write(`vehicleId:      ${vehicleId}\n`);
  process.stdout.write(`cloudApiBaseUrl:${cloudApiBaseUrl}\n`);
  process.stdout.write(`dataDirectory:  ${dataDirectory}\n`);

  const logger = new Logger("e2e-demo");
  // Constructed explicitly (rather than via Gateway's default constructor
  // params) so this script keeps its own reference to `storage` and
  // `cloudClient` for direct inspection/reuse below - these are the exact
  // same real classes Gateway's default constructor would build.
  const storage = new SqliteLocalStorage(config, logger.child("storage"));
  const cloudClient = new HttpCloudClient(cloudApiBaseUrl, cloudAuthEmail, cloudAuthPassword, logger.child("cloud-client"));
  const syncEngine = new SqliteSyncEngine(storage, cloudClient, logger.child("sync-engine"));
  const gateway = new Gateway(config, "e2e-demo", logger, storage, syncEngine);

  await gateway.start();
  process.stdout.write(`Gateway started. Initial health snapshot:\n${JSON.stringify(await gateway.getHealthSnapshot(), null, 2)}\n`);

  // --- 6D.1/6D.2: real simulator devices, real capture workflow ---------
  section("6D.2 - SIMULATOR CAPTURE (ACCEPTED case)");

  const scale = new WeighingScaleSimulator("sim-scale-01");
  const analyser = new MilkAnalyserSimulator("sim-analyser-01");
  await scale.connect();
  await analyser.connect();

  const capturedAt1 = new Date().toISOString();
  // Values chosen to fall WITHIN the seeded global quality rules (FAT
  // 3.0-6.0, SNF 8.0-10.0, TEMPERATURE 0-10.0 - see apps/api/src/seed.ts)
  // so the API's OWN business rules (not this script) determine ACCEPTED.
  scale.setNextReadings([{ weightKg: 452.5, stable: true, capturedAt: capturedAt1 }]);
  analyser.setNextReadings([{ fat: 4.2, snf: 8.6, temperature: 4.0, capturedAt: capturedAt1 }]);

  const workflow = new ReceptionCaptureWorkflow(scale, analyser, storage, gatewayId);
  const captureContext = { centreId, sourceId, vehicleId };

  const assembledInput = await workflow.readAndAssemble(captureContext);
  process.stdout.write(`Scale reading:    ${JSON.stringify(await scale.status())}\n`);
  process.stdout.write(`Assembled input (post scale+analyser read, pre-persist):\n${JSON.stringify(assembledInput, null, 2)}\n`);
  process.stdout.write(`Exactly one localIdempotencyKey generated for this logical transaction: ${assembledInput.localIdempotencyKey}\n`);

  const { localTransaction, outboxRecord, wasNewlyCreated } = await workflow.persist(assembledInput);
  process.stdout.write(`\nPersisted to SQLite local_transactions (wasNewlyCreated=${wasNewlyCreated}):\n${JSON.stringify(localTransaction, null, 2)}\n`);
  process.stdout.write(`Corresponding outbox record (status must be PENDING):\n${JSON.stringify(outboxRecord, null, 2)}\n`);
  if (outboxRecord.status !== "PENDING") {
    throw new Error(`Expected a fresh outbox record to be PENDING, got ${outboxRecord.status}`);
  }

  // --- 6D.3: real SyncEngine against the real HttpCloudClient -----------
  section("6D.3 - SYNC (real SyncEngine.tick() against the real API/Postgres)");
  process.stdout.write(`pendingSyncCount before tick: ${(await gateway.getHealthSnapshot()).pendingSyncCount}\n`);

  // tick() is SqliteSyncEngine's own public, directly-callable unit of
  // work (see sync-engine.ts's doc comment) - calling it here instead of
  // waiting out the real 15s poll interval is exercising the exact same
  // code path start()'s timer would call, just without the wait.
  await syncEngine.tick();

  const syncedOutbox = await storage.getOutboxRecordById(outboxRecord.id);
  const syncedTransaction = await storage.getLocalTransactionById(localTransaction.id);
  process.stdout.write(`\nOutbox record AFTER tick():\n${JSON.stringify(syncedOutbox, null, 2)}\n`);
  process.stdout.write(`Local transaction AFTER tick() (cloudTransactionId now set):\n${JSON.stringify(syncedTransaction, null, 2)}\n`);
  if (syncedOutbox?.status !== "SYNCED") {
    throw new Error(`Expected outbox to reach SYNCED, got ${syncedOutbox?.status} (last error: ${syncedOutbox?.lastError})`);
  }
  if (syncedTransaction?.cloudTransactionId === null || syncedTransaction?.cloudTransactionId === undefined) {
    throw new Error("Expected local transaction to receive a cloudTransactionId after sync");
  }

  const healthAfterSync = await gateway.getHealthSnapshot();
  process.stdout.write(`\nHealth snapshot after sync:\n${JSON.stringify(healthAfterSync, null, 2)}\n`);
  if (healthAfterSync.pendingSyncCount !== 0) {
    throw new Error(`Expected pendingSyncCount 0 after the only outbox item synced, got ${healthAfterSync.pendingSyncCount}`);
  }

  // --- 6D.4: idempotency / lost-response retry --------------------------
  section("6D.4 - IDEMPOTENCY (simulated lost-response retry, same localIdempotencyKey)");
  process.stdout.write(
    "Calling the REAL HttpCloudClient.sendReception() again with the EXACT SAME payload/localIdempotencyKey - " +
      "this models the real-world case the design protects against: the gateway sent the request, the cloud " +
      "committed it, but the response was lost (network blip) before the gateway saw it, so SyncEngine would " +
      "otherwise retry.\n",
  );
  const retryResult = await cloudClient.sendReception({
    localIdempotencyKey: assembledInput.localIdempotencyKey,
    centreId: assembledInput.centreId,
    sourceId: assembledInput.sourceId,
    vehicleId: assembledInput.vehicleId,
    quantityKg: assembledInput.quantityKg,
    fat: assembledInput.fat,
    snf: assembledInput.snf,
    temperature: assembledInput.temperature,
  });
  process.stdout.write(`Retry result: ${JSON.stringify(retryResult, null, 2)}\n`);
  if (retryResult.outcome === "retryable-error" || retryResult.outcome === "terminal-error") {
    throw new Error(`Expected the retry to succeed (created/duplicate), got outcome=${retryResult.outcome}: ${retryResult.message}`);
  }
  // retryResult.outcome is now narrowed to "created" | "duplicate" - both
  // carry cloudTransactionId. A genuinely idempotent retry must be
  // "duplicate" (the cloud already had this key) and must return the
  // SAME id as the original sync - "created" here would mean the cloud
  // made a SECOND row, which is exactly the bug this proves did not happen.
  if (retryResult.outcome !== "duplicate") {
    throw new Error(`Expected the retry to be recognized as a duplicate, but the cloud reported outcome="created" - a second row was made.`);
  }
  if (retryResult.cloudTransactionId !== syncedTransaction!.cloudTransactionId) {
    throw new Error(
      `Expected the retry's cloudTransactionId (${retryResult.cloudTransactionId}) to match the original sync's ` +
        `(${syncedTransaction!.cloudTransactionId}) - a mismatch would mean two different rows exist.`,
    );
  }
  process.stdout.write(
    `Confirmed: retry returned outcome="duplicate" with the SAME cloudTransactionId ` +
      `(${retryResult.cloudTransactionId}) as the original sync - no second PostgreSQL row was created.\n`,
  );

  // Also prove gateway-side (SQLite) idempotency: persist() called again
  // with the SAME already-keyed input must not create a second local
  // transaction/outbox row either.
  const secondPersist = await workflow.persist(assembledInput);
  process.stdout.write(
    `\nGateway-side (SQLite) re-persist with the same key: wasNewlyCreated=${secondPersist.wasNewlyCreated}, ` +
      `same local_transactions.id=${secondPersist.localTransaction.id === localTransaction.id}\n`,
  );
  if (secondPersist.wasNewlyCreated) {
    throw new Error("Expected SQLite createLocalTransaction() to be idempotent on localIdempotencyKey");
  }

  // --- A second, deliberately out-of-range capture -> HOLD --------------
  section("6D.2/6D.3 (continued) - a second capture that the API's real business rules put on HOLD");
  const capturedAt2 = new Date().toISOString();
  // FAT well below the seeded global rule's minValue=3.0 - the GATEWAY
  // does not evaluate quality rules itself (see reception-validation.ts's
  // doc comment: only structural checks happen client-side); HOLD here is
  // determined entirely by the real cloud API's QualityValidationService,
  // not fabricated by this script.
  scale.setNextReadings([{ weightKg: 300, stable: true, capturedAt: capturedAt2 }]);
  analyser.setNextReadings([{ fat: 1.8, snf: 8.6, temperature: 4.0, capturedAt: capturedAt2 }]);

  const holdCaptureResult = await workflow.captureReception(captureContext);
  process.stdout.write(`Second local transaction persisted, outbox id=${holdCaptureResult.outboxRecord.id}\n`);
  await syncEngine.tick();
  const holdOutbox = await storage.getOutboxRecordById(holdCaptureResult.outboxRecord.id);
  const holdTransaction = await storage.getLocalTransactionById(holdCaptureResult.localTransaction.id);
  process.stdout.write(`Outbox after tick(): ${JSON.stringify(holdOutbox, null, 2)}\n`);
  process.stdout.write(`Local transaction after tick(): ${JSON.stringify(holdTransaction, null, 2)}\n`);
  if (holdOutbox?.status !== "SYNCED") {
    throw new Error(`Expected the second item to also sync (API accepts it, just as HOLD), got ${holdOutbox?.status}`);
  }

  // --- 6D.6: final gateway status ----------------------------------------
  section("6D.6 - FINAL GATEWAY STATUS");
  const finalHealth = await gateway.getHealthSnapshot();
  process.stdout.write(`${JSON.stringify(finalHealth, null, 2)}\n`);

  await gateway.stop();

  section("DONE");
  process.stdout.write(
    `cloudTransactionId (ACCEPTED case): ${syncedTransaction!.cloudTransactionId}\n` +
      `cloudTransactionId (HOLD case):     ${holdTransaction!.cloudTransactionId}\n` +
      `localIdempotencyKey (ACCEPTED case, reused for the retry proof): ${assembledInput.localIdempotencyKey}\n` +
      `gateway.sqlite location: ${path.join(dataDirectory, "gateway.sqlite")}\n`,
  );
}

main().catch((err) => {
  process.stderr.write(`\nE2E DEMO FAILED: ${(err as Error).stack ?? (err as Error).message}\n`);
  process.exitCode = 1;
});
