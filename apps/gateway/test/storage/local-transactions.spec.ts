import { createTestStorage, sampleTransactionInput } from "../support/test-storage";
import { generateLocalIdempotencyKey } from "../../src/storage/idempotency";

describe("SqliteLocalStorage - createLocalTransaction atomicity & idempotency", () => {
  // TEST 1: Create local transaction; confirm transaction + outbox exist.
  it("TEST 1: creates a local transaction and its outbox record together", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput();
    const result = await handle.storage.createLocalTransaction(input);

    expect(result.wasNewlyCreated).toBe(true);
    expect(result.localTransaction.localIdempotencyKey).toBe(input.localIdempotencyKey);
    expect(result.localTransaction.cloudTransactionId).toBeNull();
    expect(result.outboxRecord.localTransactionId).toBe(result.localTransaction.id);
    expect(result.outboxRecord.status).toBe("PENDING");

    const fetchedTx = await handle.storage.getLocalTransactionById(result.localTransaction.id);
    const fetchedOutbox = await handle.storage.getOutboxRecordByLocalTransactionId(result.localTransaction.id);
    expect(fetchedTx).not.toBeNull();
    expect(fetchedOutbox).not.toBeNull();

    await handle.storage.close();
    handle.cleanup();
  });

  // TEST 3: Attempt duplicate local transaction using the same
  // localIdempotencyKey; confirm no duplicate local transaction is created.
  it("TEST 3: a duplicate create with the same localIdempotencyKey does not create a duplicate row", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "fixed-key-abc" });
    const first = await handle.storage.createLocalTransaction(input);
    const second = await handle.storage.createLocalTransaction(input);

    expect(first.wasNewlyCreated).toBe(true);
    expect(second.wasNewlyCreated).toBe(false);
    expect(second.localTransaction.id).toBe(first.localTransaction.id);
    expect(second.outboxRecord.id).toBe(first.outboxRecord.id);

    const all = await handle.storage.listLocalTransactions();
    expect(all.filter((t) => t.localIdempotencyKey === "fixed-key-abc")).toHaveLength(1);

    await handle.storage.close();
    handle.cleanup();
  });

  it("TEST 3b: the UNIQUE constraint itself rejects a raw duplicate INSERT (DB-level enforcement, not just app logic)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    const input = sampleTransactionInput({ localIdempotencyKey: "raw-insert-key" });
    await handle.storage.createLocalTransaction(input);

    // Bypass the application-level createLocalTransaction() entirely and
    // attempt a raw duplicate INSERT directly against the table, proving
    // the constraint - not application code - is what actually prevents
    // this, per the explicit "enforce uniqueness at the database level"
    // instruction.
    expect(() =>
      db
        .prepare(
          `INSERT INTO local_transactions
             (local_idempotency_key, centre_id, source_id, vehicle_id, quantity_kg, fat, snf, temperature, captured_at, created_at, cloud_transaction_id)
           VALUES (?, 1, 1, 1, 1, 1, 1, 1, ?, ?, NULL)`,
        )
        .run("raw-insert-key", new Date().toISOString(), new Date().toISOString()),
    ).toThrow(/UNIQUE constraint failed/);

    await handle.storage.close();
    handle.cleanup();
  });

  // TEST 4: Force a failure during the transaction that creates the local
  // transaction + outbox; confirm SQLite rollback leaves no partial state.
  it("TEST 4: a failure between the two inserts rolls back completely - no orphaned local_transaction", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;

    // Force the SECOND insert (outbox_records) inside
    // createLocalTransaction's atomic block to fail, by pre-creating a
    // conflicting outbox row that collides on local_idempotency_key
    // UNIQUE - but for a DIFFERENT (fake) local_transaction_id, so the
    // FIRST insert (local_transactions) will still succeed before the
    // conflict is hit. This is a real SQLite constraint violation, not a
    // mock, and it happens genuinely mid-transaction.
    const key = "forced-failure-key";
    // Insert a decoy local_transaction to own local_transaction_id=999999's FK target.
    db.prepare(
      `INSERT INTO local_transactions
         (id, local_idempotency_key, centre_id, source_id, vehicle_id, quantity_kg, fat, snf, temperature, captured_at, created_at, cloud_transaction_id)
       VALUES (999999, 'decoy-key-unrelated', 1, 1, 1, 1, 1, 1, 1, ?, ?, NULL)`,
    ).run(new Date().toISOString(), new Date().toISOString());
    db.prepare(
      `INSERT INTO outbox_records
         (local_transaction_id, local_idempotency_key, status, attempt_count, last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
       VALUES (999999, ?, 'PENDING', 0, NULL, ?, NULL, NULL, ?, ?)`,
    ).run(key, new Date().toISOString(), new Date().toISOString(), new Date().toISOString());

    const beforeCount = (db.prepare("SELECT COUNT(*) as c FROM local_transactions").get() as { c: number }).c;

    const input = sampleTransactionInput({ localIdempotencyKey: key });
    await expect(handle.storage.createLocalTransaction(input)).rejects.toThrow(/UNIQUE constraint failed/);

    const afterCount = (db.prepare("SELECT COUNT(*) as c FROM local_transactions").get() as { c: number }).c;

    // Exactly the decoy row from before the call - the attempted new
    // local_transactions row was rolled back along with the outbox insert
    // that failed, so no orphaned local_transaction was left behind.
    expect(afterCount).toBe(beforeCount);
    const orphan = await handle.storage.getLocalTransactionByIdempotencyKey(key);
    expect(orphan).toBeNull();

    await handle.storage.close();
    handle.cleanup();
  });

  // TEST 5: Create multiple pending transactions; restart gateway; confirm
  // all remain available for later synchronization.
  it("TEST 5: multiple pending transactions all survive a close()+reopen (restart)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const inputs = [sampleTransactionInput(), sampleTransactionInput(), sampleTransactionInput()];
    for (const input of inputs) {
      await handle.storage.createLocalTransaction(input);
    }
    await handle.storage.close();

    const { SqliteLocalStorage } = await import("../../src/storage/sqlite-local-storage");
    const { Logger } = await import("../../src/logging/logger");
    const reopened = new SqliteLocalStorage(handle.config, new Logger("test"));
    await reopened.init();

    const allTx = await reopened.listLocalTransactions();
    const allOutbox = await reopened.listOutboxRecords();
    expect(allTx).toHaveLength(3);
    expect(allOutbox).toHaveLength(3);
    expect(allOutbox.every((o) => o.status === "PENDING")).toBe(true);

    for (const input of inputs) {
      const tx = allTx.find((t) => t.localIdempotencyKey === input.localIdempotencyKey);
      expect(tx).toBeDefined();
    }

    await reopened.close();
    handle.cleanup();
  });

  it("generateLocalIdempotencyKey produces unique, gateway-prefixed keys", () => {
    const a = generateLocalIdempotencyKey("gw-blr-001");
    const b = generateLocalIdempotencyKey("gw-blr-001");
    expect(a).not.toBe(b);
    expect(a.startsWith("gw-blr-001:")).toBe(true);
  });

  // New for the local (edge) HTTP API - see docs/gateway-architecture.md
  // §17c and src/local-api/local-api-server.ts. Both queries are plain
  // SELECTs against the existing local_transactions table (no schema
  // change), so these tests exist to prove the WHERE/ORDER BY/LIMIT
  // behavior is exactly right, not to exercise anything new in
  // createLocalTransaction() itself.
  describe("listLocalTransactionsInRange (backs GET /local/today)", () => {
    it("returns only transactions with capturedAt in [start, end)", async () => {
      const handle = createTestStorage();
      await handle.storage.init();

      await handle.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: "2026-08-22T23:59:59.000Z" }));
      const inRangeStart = await handle.storage.createLocalTransaction(
        sampleTransactionInput({ capturedAt: "2026-08-23T00:00:00.000Z" }),
      );
      const inRangeMid = await handle.storage.createLocalTransaction(
        sampleTransactionInput({ capturedAt: "2026-08-23T12:00:00.000Z" }),
      );
      await handle.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: "2026-08-24T00:00:00.000Z" }));

      const inRange = await handle.storage.listLocalTransactionsInRange(
        "2026-08-23T00:00:00.000Z",
        "2026-08-24T00:00:00.000Z",
      );

      expect(inRange.map((t) => t.id).sort()).toEqual(
        [inRangeStart.localTransaction.id, inRangeMid.localTransaction.id].sort(),
      );

      await handle.storage.close();
      handle.cleanup();
    });

    it("returns an empty array when nothing falls in range", async () => {
      const handle = createTestStorage();
      await handle.storage.init();

      await handle.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: "2026-01-01T00:00:00.000Z" }));
      const inRange = await handle.storage.listLocalTransactionsInRange(
        "2026-08-23T00:00:00.000Z",
        "2026-08-24T00:00:00.000Z",
      );
      expect(inRange).toEqual([]);

      await handle.storage.close();
      handle.cleanup();
    });
  });

  describe("listRecentLocalTransactions (backs GET /local/transactions?limit=N)", () => {
    it("returns the most recent transactions, newest first, capped at limit", async () => {
      const handle = createTestStorage();
      await handle.storage.init();

      const created = [];
      for (let i = 0; i < 5; i++) {
        created.push(await handle.storage.createLocalTransaction(sampleTransactionInput()));
      }

      const recent = await handle.storage.listRecentLocalTransactions(3);
      expect(recent).toHaveLength(3);
      expect(recent.map((t) => t.id)).toEqual(
        [created[4].localTransaction.id, created[3].localTransaction.id, created[2].localTransaction.id],
      );

      await handle.storage.close();
      handle.cleanup();
    });

    it("returns everything (not an error) when limit exceeds the row count", async () => {
      const handle = createTestStorage();
      await handle.storage.init();

      await handle.storage.createLocalTransaction(sampleTransactionInput());
      const recent = await handle.storage.listRecentLocalTransactions(50);
      expect(recent).toHaveLength(1);

      await handle.storage.close();
      handle.cleanup();
    });
  });
});
