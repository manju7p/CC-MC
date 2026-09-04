import { SqliteSyncEngine } from "../../src/sync/sync-engine";
import { FakeCloudClient } from "../support/fake-cloud-client";
import { createTestStorage, sampleTransactionInput, silentLogger } from "../support/test-storage";

describe("SqliteSyncEngine.tick()", () => {
  it("claims an eligible PENDING item, delivers it, and marks it SYNCED on success", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    const { outboxRecord, localTransaction } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("SYNCED");
    const refreshedTx = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(refreshedTx?.cloudTransactionId).not.toBeNull();

    expect(cloudClient.getCalls()).toHaveLength(1);
    expect(cloudClient.getCalls()[0].localIdempotencyKey).toBe(localTransaction.localIdempotencyKey);

    await handle.storage.close();
    handle.cleanup();
  });

  it("treats a 'duplicate' cloud response as success (server-side idempotency already handled it)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "duplicate" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("SYNCED");

    await handle.storage.close();
    handle.cleanup();
  });

  it("schedules a retry with backoff on a retryable-error, and does NOT retry again before nextAttemptAt (no hammering)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "retryable-error", message: "network timeout" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    const afterFirstFailure = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(afterFirstFailure?.status).toBe("PENDING");
    expect(afterFirstFailure?.attemptCount).toBe(1);
    expect(afterFirstFailure?.lastError).toBe("network timeout");
    expect(new Date(afterFirstFailure!.nextAttemptAt).getTime()).toBeGreaterThan(Date.now());

    // Ticking again immediately must NOT re-attempt delivery - the item
    // isn't eligible yet (nextAttemptAt is in the future). This is the
    // concrete proof of "avoid hammering the cloud when connectivity is
    // unavailable."
    await engine.tick();
    expect(cloudClient.getCalls()).toHaveLength(1); // still just the one attempt

    await handle.storage.close();
    handle.cleanup();
  });

  it("retries and eventually succeeds once nextAttemptAt has passed and the cloud starts accepting", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient();
    const { outboxRecord, localTransaction } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    cloudClient.scriptFor(localTransaction.localIdempotencyKey, [
      { outcome: "retryable-error", message: "attempt 1 fails" },
      { outcome: "created" },
    ]);
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    await engine.tick(); // fails, schedules retry
    let refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("PENDING");
    expect(refreshed?.attemptCount).toBe(1);

    // Force the retry to be due now, rather than sleeping in a test for
    // real backoff time - deterministic, per the checkpoint's "make retry
    // behavior deterministic enough to test" instruction.
    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    db.prepare("UPDATE outbox_records SET next_attempt_at = ? WHERE id = ?").run(new Date().toISOString(), outboxRecord.id);

    await engine.tick(); // now eligible, succeeds
    refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("SYNCED");
    expect(cloudClient.getCalls()).toHaveLength(2);

    await handle.storage.close();
    handle.cleanup();
  });

  it("marks an item FAILED (terminal) after a terminal-error, without retrying", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "terminal-error", message: "invalid sourceId" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("FAILED");
    expect(refreshed?.lastError).toBe("invalid sourceId");

    await handle.storage.close();
    handle.cleanup();
  });

  it("marks an item FAILED after exceeding maxAttempts of retryable errors, and stops retrying", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "retryable-error", message: "always fails" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), {
      pollIntervalMs: 1000,
      batchSize: 10,
      maxAttempts: 3,
      staleProcessingThresholdMs: 60_000,
    });

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;

    for (let i = 0; i < 3; i++) {
      await engine.tick();
      // Force immediate eligibility for the next attempt instead of
      // waiting out real backoff time.
      db.prepare("UPDATE outbox_records SET next_attempt_at = ? WHERE id = ?").run(new Date().toISOString(), outboxRecord.id);
    }

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("FAILED");
    expect(cloudClient.getCalls()).toHaveLength(3); // exactly maxAttempts, no more

    // A further tick must not attempt delivery again - FAILED is terminal.
    await engine.tick();
    expect(cloudClient.getCalls()).toHaveLength(3);

    await handle.storage.close();
    handle.cleanup();
  });

  it("processes multiple eligible items in one tick, up to batchSize", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await handle.storage.createLocalTransaction(sampleTransactionInput());

    await engine.tick();

    const all = await handle.storage.listOutboxRecords();
    expect(all.every((o) => o.status === "SYNCED")).toBe(true);
    expect(cloudClient.getCalls()).toHaveLength(3);

    await handle.storage.close();
    handle.cleanup();
  });

  it("recovers a stale PROCESSING item at the start of a tick and makes it eligible again", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), {
      pollIntervalMs: 1000,
      batchSize: 10,
      maxAttempts: 10,
      staleProcessingThresholdMs: 1000, // 1 second - short, for a fast test
    });

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const longAgo = new Date(Date.now() - 60_000).toISOString();
    await handle.storage.claimOutboxItem(outboxRecord.id, longAgo); // simulate a stuck claim from a hung prior tick

    await engine.tick();

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    // Recovered to PENDING, re-claimed, and delivered - all within the same tick.
    expect(refreshed?.status).toBe("SYNCED");

    await handle.storage.close();
    handle.cleanup();
  });

  // Checkpoint 6D: the optional onCloudConnected callback (used by
  // gateway.ts to wire HealthService.setCloudConnectivity("CONNECTED")).
  it("calls onCloudConnected after a successful ('created') sync", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const onCloudConnected = jest.fn();
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), undefined, onCloudConnected);

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    expect(onCloudConnected).toHaveBeenCalledTimes(1);

    await handle.storage.close();
    handle.cleanup();
  });

  it("calls onCloudConnected after a 'duplicate' sync outcome too", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "duplicate" });
    const onCloudConnected = jest.fn();
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), undefined, onCloudConnected);

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    expect(onCloudConnected).toHaveBeenCalledTimes(1);

    await handle.storage.close();
    handle.cleanup();
  });

  it("does NOT call onCloudConnected on a retryable-error - never guesses at connectivity from an ambiguous failure", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "retryable-error", message: "network timeout" });
    const onCloudConnected = jest.fn();
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), undefined, onCloudConnected);

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    expect(onCloudConnected).not.toHaveBeenCalled();

    await handle.storage.close();
    handle.cleanup();
  });

  it("does NOT call onCloudConnected on a terminal-error", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "terminal-error", message: "invalid sourceId" });
    const onCloudConnected = jest.fn();
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), undefined, onCloudConnected);

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await engine.tick();

    expect(onCloudConnected).not.toHaveBeenCalled();

    await handle.storage.close();
    handle.cleanup();
  });

  it("is safe to omit onCloudConnected entirely (existing default behavior unchanged)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient({ outcome: "created" });
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());

    await handle.storage.createLocalTransaction(sampleTransactionInput());
    await expect(engine.tick()).resolves.toBeUndefined();

    await handle.storage.close();
    handle.cleanup();
  });

  it("start()/stop() are idempotent and stop() actually clears the interval (process can exit cleanly)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    const cloudClient = new FakeCloudClient();
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger(), {
      pollIntervalMs: 50,
      batchSize: 10,
      maxAttempts: 10,
      staleProcessingThresholdMs: 60_000,
    });

    await engine.start();
    await engine.start(); // no-op, must not create a second timer
    await engine.stop();
    await engine.stop(); // no-op, must not throw

    await handle.storage.close();
    handle.cleanup();
  });
});
