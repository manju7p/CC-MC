import { createTestStorage, sampleTransactionInput } from "../support/test-storage";

describe("SqliteLocalStorage - outbox state machine", () => {
  it("claimOutboxItem transitions PENDING -> PROCESSING and stamps claimedAt", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    const claimed = await handle.storage.claimOutboxItem(outboxRecord.id, now);

    expect(claimed).toBe(true);
    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("PROCESSING");
    expect(refreshed?.claimedAt).toBe(now);

    await handle.storage.close();
    handle.cleanup();
  });

  it("claimOutboxItem returns false and is a no-op if the row is not PENDING", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    await handle.storage.claimOutboxItem(outboxRecord.id, now); // -> PROCESSING

    const secondClaim = await handle.storage.claimOutboxItem(outboxRecord.id, now);
    expect(secondClaim).toBe(false);

    await handle.storage.close();
    handle.cleanup();
  });

  it("findEligibleOutboxItems only returns PENDING rows due now, oldest first", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const past = new Date(Date.now() - 60_000).toISOString();
    const future = new Date(Date.now() + 60_000).toISOString();

    const a = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const b = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const c = await handle.storage.createLocalTransaction(sampleTransactionInput());

    // Manually backdate/forward-date next_attempt_at via markOutboxRetry's
    // effect is awkward here, so reach into the DB directly for this setup -
    // this test is about the SELECT query's filtering, not about retry.
    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    db.prepare("UPDATE outbox_records SET next_attempt_at = ? WHERE id = ?").run(past, a.outboxRecord.id);
    db.prepare("UPDATE outbox_records SET next_attempt_at = ? WHERE id = ?").run(future, b.outboxRecord.id);
    // c stays at its original next_attempt_at (creation time, <= now)

    // Captured AFTER a/b/c are created - querying "eligible as of now" only
    // makes sense against a `now` that is actually >= every row's creation
    // time. An earlier version of this test captured `now` before creating
    // a/b/c and flaked: c's next_attempt_at (stamped at its own creation,
    // milliseconds after that early `now`) ended up just after it, so c
    // was wrongly excluded - a bug in the test, not in
    // findEligibleOutboxItems's query.
    const now = new Date().toISOString();
    const eligible = await handle.storage.findEligibleOutboxItems(now, 10);
    const eligibleIds = eligible.map((o) => o.id);

    expect(eligibleIds).toContain(a.outboxRecord.id);
    expect(eligibleIds).not.toContain(b.outboxRecord.id); // future - not eligible yet
    expect(eligibleIds).toContain(c.outboxRecord.id);
    expect(eligibleIds.indexOf(a.outboxRecord.id)).toBeLessThan(eligibleIds.indexOf(c.outboxRecord.id)); // oldest first

    await handle.storage.close();
    handle.cleanup();
  });

  it("markOutboxSynced sets outbox SYNCED and stamps local_transactions.cloudTransactionId atomically", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { localTransaction, outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    await handle.storage.markOutboxSynced(outboxRecord.id, 555, now);

    const refreshedOutbox = await handle.storage.getOutboxRecordById(outboxRecord.id);
    const refreshedTx = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(refreshedOutbox?.status).toBe("SYNCED");
    expect(refreshedTx?.cloudTransactionId).toBe(555);

    await handle.storage.close();
    handle.cleanup();
  });

  it("markOutboxRetry increments attemptCount, applies backoff, and returns to PENDING", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    await handle.storage.claimOutboxItem(outboxRecord.id, now);
    await handle.storage.markOutboxRetry(outboxRecord.id, "simulated timeout", now);

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("PENDING");
    expect(refreshed?.attemptCount).toBe(1);
    expect(refreshed?.lastError).toBe("simulated timeout");
    expect(refreshed?.claimedAt).toBeNull();
    expect(new Date(refreshed!.nextAttemptAt).getTime()).toBeGreaterThan(new Date(now).getTime());

    await handle.storage.close();
    handle.cleanup();
  });

  it("markOutboxFailed sets outbox FAILED (terminal) and is excluded from eligibility", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    await handle.storage.markOutboxFailed(outboxRecord.id, "terminal validation error", now);

    const refreshed = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(refreshed?.status).toBe("FAILED");

    const eligible = await handle.storage.findEligibleOutboxItems(new Date().toISOString(), 10);
    expect(eligible.map((o) => o.id)).not.toContain(outboxRecord.id);

    await handle.storage.close();
    handle.cleanup();
  });

  // TEST 2: Create transaction; mark outbox as PROCESSING; simulate
  // process death; restart; confirm it becomes eligible for retry.
  it("TEST 2: an outbox row stuck PROCESSING when the process died becomes eligible again after restart", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await handle.storage.claimOutboxItem(outboxRecord.id, new Date().toISOString());

    const midFlight = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(midFlight?.status).toBe("PROCESSING");

    // Simulate process death: close the connection WITHOUT ever calling
    // markOutboxSynced/markOutboxRetry/markOutboxFailed - exactly what a
    // crash mid-delivery would leave behind (no clean-shutdown code path
    // runs on a real crash either).
    await handle.storage.close();

    const { SqliteLocalStorage } = await import("../../src/storage/sqlite-local-storage");
    const { Logger } = await import("../../src/logging/logger");
    const restarted = new SqliteLocalStorage(handle.config, new Logger("test"));
    await restarted.init(); // init() itself runs the startup recovery sweep

    const afterRestart = await restarted.getOutboxRecordById(outboxRecord.id);
    expect(afterRestart?.status).toBe("PENDING"); // NOT stuck in PROCESSING forever
    expect(afterRestart?.claimedAt).toBeNull();

    const eligible = await restarted.findEligibleOutboxItems(new Date().toISOString(), 10);
    expect(eligible.map((o) => o.id)).toContain(outboxRecord.id);

    await restarted.close();
    handle.cleanup();
  });

  it("recoverStaleProcessing with a real threshold does NOT reclaim a recently-claimed item (avoids stealing genuinely in-flight work)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();
    await handle.storage.claimOutboxItem(outboxRecord.id, now);

    const recoveredCount = await handle.storage.recoverStaleProcessing(now, 5 * 60_000); // 5 minute staleness threshold
    expect(recoveredCount).toBe(0);

    const stillProcessing = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(stillProcessing?.status).toBe("PROCESSING");

    await handle.storage.close();
    handle.cleanup();
  });

  it("recoverStaleProcessing DOES reclaim an item claimed longer ago than the threshold", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const claimedAt = new Date(Date.now() - 10 * 60_000).toISOString(); // claimed 10 minutes ago
    await handle.storage.claimOutboxItem(outboxRecord.id, claimedAt);

    const now = new Date().toISOString();
    const recoveredCount = await handle.storage.recoverStaleProcessing(now, 5 * 60_000); // 5 minute threshold
    expect(recoveredCount).toBe(1);

    const recovered = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(recovered?.status).toBe("PENDING");

    await handle.storage.close();
    handle.cleanup();
  });

  // Checkpoint 6B: init({ recoverStaleProcessing: false }) must skip the
  // startup sweep entirely (for a `--status` diagnostic call that may run
  // concurrently with an already-running service against the same
  // database file - see storage.types.ts's init() doc comment), while a
  // default init() with no options must still sweep exactly as before
  // (TEST 2, above).
  it("init({ recoverStaleProcessing: false }) leaves a stuck PROCESSING row untouched", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await handle.storage.claimOutboxItem(outboxRecord.id, new Date().toISOString());

    const midFlight = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(midFlight?.status).toBe("PROCESSING");

    // Simulate process death, same as TEST 2, then "reopen" the same
    // database - but this time as the concurrent diagnostic caller would,
    // opting OUT of the startup sweep.
    await handle.storage.close();

    const { SqliteLocalStorage } = await import("../../src/storage/sqlite-local-storage");
    const { Logger } = await import("../../src/logging/logger");
    const reopened = new SqliteLocalStorage(handle.config, new Logger("test"));
    await reopened.init({ recoverStaleProcessing: false });

    const stillProcessing = await reopened.getOutboxRecordById(outboxRecord.id);
    expect(stillProcessing?.status).toBe("PROCESSING"); // NOT reclaimed - sweep was skipped
    expect(stillProcessing?.claimedAt).not.toBeNull();

    // The pendingSyncCount signal must still be intact even with the sweep
    // skipped - countPendingOutbox() counts PROCESSING rows too, so no
    // status-reporting regression results from skipping the sweep.
    expect(await reopened.countPendingOutbox()).toBe(1);

    await reopened.close();
    handle.cleanup();
  });

  it("init() with no options (default) still sweeps stuck PROCESSING rows, unchanged from before", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const { outboxRecord } = await handle.storage.createLocalTransaction(sampleTransactionInput());
    await handle.storage.claimOutboxItem(outboxRecord.id, new Date().toISOString());
    await handle.storage.close();

    const { SqliteLocalStorage } = await import("../../src/storage/sqlite-local-storage");
    const { Logger } = await import("../../src/logging/logger");
    const reopened = new SqliteLocalStorage(handle.config, new Logger("test"));
    await reopened.init(); // no options -> default behavior, sweep runs

    const afterRestart = await reopened.getOutboxRecordById(outboxRecord.id);
    expect(afterRestart?.status).toBe("PENDING");
    expect(afterRestart?.claimedAt).toBeNull();

    await reopened.close();
    handle.cleanup();
  });

  it("countPendingOutbox counts PENDING and PROCESSING but not SYNCED/FAILED", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const a = await handle.storage.createLocalTransaction(sampleTransactionInput()); // stays PENDING
    const b = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const c = await handle.storage.createLocalTransaction(sampleTransactionInput());
    const now = new Date().toISOString();

    await handle.storage.claimOutboxItem(b.outboxRecord.id, now); // -> PROCESSING
    await handle.storage.markOutboxSynced(c.outboxRecord.id, 1, now); // -> SYNCED

    expect(await handle.storage.countPendingOutbox()).toBe(2); // a (PENDING) + b (PROCESSING)
    void a;

    await handle.storage.close();
    handle.cleanup();
  });
});
