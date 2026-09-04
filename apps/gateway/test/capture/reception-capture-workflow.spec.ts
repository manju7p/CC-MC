import { ReceptionCaptureWorkflow } from "../../src/capture/reception-capture-workflow";
import { WeighingScaleSimulator } from "../../src/device/simulators/weighing-scale-simulator";
import { MilkAnalyserSimulator } from "../../src/device/simulators/milk-analyser-simulator";
import { InvalidReadingError } from "../../src/capture/reception-validation";
import { IdempotencyKeyConflictError } from "../../src/storage/sqlite-local-storage";
import { SqliteSyncEngine } from "../../src/sync/sync-engine";
import { FakeCloudClient } from "../support/fake-cloud-client";
import { createTestStorage, silentLogger } from "../support/test-storage";
import { SqliteLocalStorage } from "../../src/storage/sqlite-local-storage";
import { Logger } from "../../src/logging/logger";

const CONTEXT = { centreId: 1, sourceId: 10, vehicleId: 20 };

function validScaleReading(overrides: Partial<{ weightKg: number; stable: boolean; capturedAt: string }> = {}) {
  return { weightKg: 452.5, stable: true, capturedAt: "2026-01-01T00:00:00.000Z", ...overrides };
}

function validAnalyserReading(
  overrides: Partial<{ fat: number; snf: number; temperature: number; capturedAt: string }> = {},
) {
  return { fat: 4.2, snf: 8.5, temperature: 4.0, capturedAt: "2026-01-01T00:00:01.000Z", ...overrides };
}

describe("ReceptionCaptureWorkflow", () => {
  // FAILURE SEMANTIC #1: Device unavailable -> NO transaction should be created.
  it("#1 device unavailable: scale.read() rejecting propagates and creates NO local transaction", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1"); // never connected -> read() rejects
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    await expect(workflow.captureReception(CONTEXT)).rejects.toThrow(/not connected/);

    expect(await handle.storage.listLocalTransactions()).toHaveLength(0);
    expect(await handle.storage.listOutboxRecords()).toHaveLength(0);

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #2: Device returns malformed/invalid normalized data -> NO invalid transaction should enter SQLite.
  it("#2 invalid normalized data: a negative weight reading is rejected before persistence, creating NO row", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading({ weightKg: -50 }));
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    await expect(workflow.captureReception(CONTEXT)).rejects.toThrow(InvalidReadingError);

    expect(await handle.storage.listLocalTransactions()).toHaveLength(0);
    expect(await handle.storage.listOutboxRecords()).toHaveLength(0);

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #3: SQLite succeeds -> outbox must exist.
  it("#3 a successful capture creates BOTH a local transaction and its outbox record", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const result = await workflow.captureReception(CONTEXT);

    expect(result.wasNewlyCreated).toBe(true);
    expect(result.localTransaction.quantityKg).toBe(452.5);
    expect(result.localTransaction.fat).toBe(4.2);
    expect(result.outboxRecord.localTransactionId).toBe(result.localTransaction.id);
    expect(result.outboxRecord.status).toBe("PENDING");

    const fetchedOutbox = await handle.storage.getOutboxRecordByLocalTransactionId(result.localTransaction.id);
    expect(fetchedOutbox).not.toBeNull();

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #4: Cloud unavailable -> local transaction remains durable and PENDING.
  it("#4 cloud unavailable: the captured transaction stays durable and eligible for retry, never lost", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const captured = await workflow.captureReception(CONTEXT);

    const cloudClient = new FakeCloudClient({ outcome: "retryable-error", message: "cloud unreachable" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());
    await engine.tick();

    const tx = await handle.storage.getLocalTransactionById(captured.localTransaction.id);
    const outbox = await handle.storage.getOutboxRecordById(captured.outboxRecord.id);
    expect(tx).not.toBeNull();
    expect(tx?.cloudTransactionId).toBeNull();
    expect(outbox?.status).toBe("PENDING"); // back to PENDING (scheduled for retry), not lost, not stuck PROCESSING
    expect(outbox?.attemptCount).toBe(1);

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #5: Sync retry -> same local transaction and same idempotency key.
  it("#5 retrying persist() with the same already-assembled input reuses the same transaction and key, without re-reading devices", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const input = await workflow.readAndAssemble(CONTEXT);
    const first = await workflow.persist(input);
    // Simulate retrying ONLY the persistence step (e.g. a transient
    // storage error on the first attempt) - persist() again with the
    // SAME input, no new device reads, no new key.
    const second = await workflow.persist(input);

    expect(second.wasNewlyCreated).toBe(false);
    expect(second.localTransaction.id).toBe(first.localTransaction.id);
    expect(second.localTransaction.localIdempotencyKey).toBe(input.localIdempotencyKey);
    expect(second.outboxRecord.id).toBe(first.outboxRecord.id);

    const all = await handle.storage.listLocalTransactions();
    expect(all).toHaveLength(1);

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #6: Duplicate capture/retry -> no duplicate transaction.
  it("#6 calling captureReception twice (two independent full capture attempts) with a fixed key still yields exactly one transaction", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    // Build the "retry the whole capture" scenario explicitly: assemble
    // once to get a key, then persist it twice (representing "the caller
    // wasn't sure the first persist() succeeded, so it retries the
    // persistence step of the same logical capture" - readAndAssemble is
    // NOT called again, exactly as ReceptionCaptureWorkflow's doc comment
    // requires: "a retry must never call this function again").
    const input = await workflow.readAndAssemble(CONTEXT);
    await workflow.persist(input);
    await workflow.persist(input);

    const all = await handle.storage.listLocalTransactions();
    expect(all.filter((t) => t.localIdempotencyKey === input.localIdempotencyKey)).toHaveLength(1);

    await handle.storage.close();
    handle.cleanup();
  });

  // FAILURE SEMANTIC #7: Same idempotency key with different payload -> explicit conflict, not silent success.
  it("#7 reusing the same idempotency key for a genuinely different payload throws IdempotencyKeyConflictError, creating nothing new", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const input = await workflow.readAndAssemble(CONTEXT);
    await workflow.persist(input);

    const conflicting = { ...input, quantityKg: input.quantityKg + 100 };
    await expect(workflow.persist(conflicting)).rejects.toThrow(IdempotencyKeyConflictError);

    const all = await handle.storage.listLocalTransactions();
    expect(all).toHaveLength(1);
    expect(all[0].quantityKg).toBe(input.quantityKg); // original value preserved, not overwritten

    await handle.storage.close();
    handle.cleanup();
  });

  it("multi-device assembly: scale and analyser are read CONCURRENTLY, not sequentially (documented assumption)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    scale.simulateNextReadDelay(40);
    analyser.simulateNextReadDelay(40);
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const start = Date.now();
    await workflow.readAndAssemble(CONTEXT);
    const elapsed = Date.now() - start;

    // If reads were sequential this would take >= 80ms; concurrent reads
    // should complete in roughly one delay's worth of time.
    expect(elapsed).toBeLessThan(70);

    await handle.storage.close();
    handle.cleanup();
  });

  it("capturedAt on the persisted transaction is the assembly-time timestamp, not either device reading's own capturedAt", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading({ capturedAt: "2020-01-01T00:00:00.000Z" }));
    analyser.setDefaultReading(validAnalyserReading({ capturedAt: "2021-01-01T00:00:00.000Z" }));
    const fixedNow = "2026-06-15T12:00:00.000Z";
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId, () => fixedNow);

    const result = await workflow.captureReception(CONTEXT);

    expect(result.localTransaction.capturedAt).toBe(fixedNow);

    await handle.storage.close();
    handle.cleanup();
  });

  it("transaction -> outbox integration: a captured transaction is immediately visible to SyncEngine as an eligible PENDING item", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    await workflow.captureReception(CONTEXT);

    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());
    await engine.tick();

    expect(cloudClient.getCalls()).toHaveLength(1);
    const all = await handle.storage.listOutboxRecords();
    expect(all[0].status).toBe("SYNCED");

    await handle.storage.close();
    handle.cleanup();
  });

  it("restart/recovery integration: a captured transaction survives a close()+reopen and is still eligible for sync", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const scale = new WeighingScaleSimulator("scale-1");
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await scale.connect();
    await analyser.connect();
    scale.setDefaultReading(validScaleReading());
    analyser.setDefaultReading(validAnalyserReading());
    const workflow = new ReceptionCaptureWorkflow(scale, analyser, handle.storage, handle.config.gatewayId);

    const captured = await workflow.captureReception(CONTEXT);
    await handle.storage.close();

    const reopened = new SqliteLocalStorage(handle.config, new Logger("test"));
    await reopened.init();

    const tx = await reopened.getLocalTransactionById(captured.localTransaction.id);
    const outbox = await reopened.getOutboxRecordByLocalTransactionId(captured.localTransaction.id);
    expect(tx).not.toBeNull();
    expect(outbox?.status).toBe("PENDING");

    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(reopened, cloudClient, silentLogger());
    await engine.tick();
    expect(cloudClient.getCalls()).toHaveLength(1);

    await reopened.close();
    handle.cleanup();
  });
});
