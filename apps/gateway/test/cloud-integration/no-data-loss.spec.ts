import { HttpCloudClient } from "../../src/sync/http-cloud-client";
import { SqliteSyncEngine } from "../../src/sync/sync-engine";
import { createTestStorage, sampleTransactionInput, silentLogger } from "../support/test-storage";
import {
  BLR_CENTRE_ID,
  CLOUD_TEST_BASE_URL,
  GATEWAY_BLR_EMAIL,
  GATEWAY_BLR_PASSWORD,
  countReceptionRowsForKey,
} from "./support/live-cloud-fixture";

/**
 * THE MANDATORY "NO DATA LOSS" SCENARIO (Checkpoint 5):
 *
 *   Device -> SQLite -> cloud unavailable   =>  transaction NOT lost
 *   Cloud becomes available -> SyncEngine retries -> PostgreSQL receives
 *   the transaction -> SQLite marks it SYNCED.
 *
 * Distinct from critical-failure-scenario.spec.ts (which proves a lost
 * RESPONSE after the cloud already committed doesn't create a duplicate)
 * - this test proves a genuinely UNREACHABLE cloud at capture time never
 * loses the transaction at all, and that the very next real opportunity
 * to sync (a normal SyncEngine.tick()) picks it up and delivers it,
 * without the caller doing anything special.
 *
 * "Device -> SQLite" is exercised via LocalStorage.createLocalTransaction
 * directly, exactly as Checkpoint 4's capture workflow does after a
 * device reading is assembled into a CreateLocalTransactionInput - see
 * src/capture/reception-capture-workflow.ts. Re-driving a device
 * simulator through the full capture workflow here would only add
 * indirection; the sync engine's job (this checkpoint's scope) starts
 * once a LocalTransaction+PENDING outbox row already exist.
 */
describe("No data loss (mandatory): cloud unavailable at capture time never loses the transaction", () => {
  it("stays durably PENDING while the cloud is unreachable, then syncs for real once it becomes available", async () => {
    const handle = createTestStorage({
      centreId: BLR_CENTRE_ID,
      // Deliberately unreachable during the first tick: nothing listens
      // on this port. A real DNS-resolvable host with a closed/unbound
      // port gives a genuine ECONNREFUSED, not a mock.
      cloudApiBaseUrl: "http://127.0.0.1:1",
      cloudAuthEmail: GATEWAY_BLR_EMAIL,
      cloudAuthPassword: GATEWAY_BLR_PASSWORD,
    });
    await handle.storage.init();

    const input = sampleTransactionInput({ centreId: BLR_CENTRE_ID, sourceId: 1, vehicleId: 1 });
    const { localTransaction, outboxRecord } = await handle.storage.createLocalTransaction(input);

    // --- Phase 1: cloud unavailable ---
    const unreachableClient = new HttpCloudClient(
      "http://127.0.0.1:1",
      GATEWAY_BLR_EMAIL,
      GATEWAY_BLR_PASSWORD,
      silentLogger(),
    );
    const engineWhileDown = new SqliteSyncEngine(handle.storage, unreachableClient, silentLogger());
    await engineWhileDown.tick();

    // The transaction is NOT lost - it is durably back in SQLite as
    // PENDING (eligible for the next retry), never silently dropped.
    const afterFailedTick = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(afterFailedTick?.status).toBe("PENDING");
    expect(afterFailedTick?.attemptCount).toBeGreaterThanOrEqual(1);
    const stillUnsynced = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(stillUnsynced?.cloudTransactionId).toBeNull();

    // Nothing was ever created cloud-side while it was unreachable.
    expect(countReceptionRowsForKey(localTransaction.localIdempotencyKey)).toBe(0);

    // The failed attempt scheduled a real backoff delay (computeBackoffDelayMs,
    // src/storage/backoff.ts: 1s for attemptCount=1) before the row becomes
    // eligible again - waiting it out here rather than ticking again
    // immediately is what makes this a faithful "cloud becomes available a
    // moment later" recovery, not a race against the outbox's own backoff.
    await new Promise((resolve) => setTimeout(resolve, 1_200));

    // --- Phase 2: cloud becomes available ---
    const realClient = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());
    const engineOnceUp = new SqliteSyncEngine(handle.storage, realClient, silentLogger());
    await engineOnceUp.tick();

    const finalOutbox = await handle.storage.getOutboxRecordById(outboxRecord.id);
    const finalTx = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(finalOutbox?.status).toBe("SYNCED");
    expect(finalTx?.cloudTransactionId).not.toBeNull();
    expect(countReceptionRowsForKey(localTransaction.localIdempotencyKey)).toBe(1);

    await handle.storage.close();
    handle.cleanup();
  });
});
