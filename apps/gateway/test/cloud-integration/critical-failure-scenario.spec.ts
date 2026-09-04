import { HttpCloudClient } from "../../src/sync/http-cloud-client";
import { SqliteSyncEngine } from "../../src/sync/sync-engine";
import { Logger } from "../../src/logging/logger";
import { createTestStorage, sampleTransactionInput, silentLogger } from "../support/test-storage";
import {
  BLR_CENTRE_ID,
  CLOUD_TEST_BASE_URL,
  GATEWAY_BLR_EMAIL,
  GATEWAY_BLR_PASSWORD,
  countReceptionCreateAuditEvents,
  countReceptionRowsForKey,
} from "./support/live-cloud-fixture";

/**
 * THE MANDATORY CRITICAL FAILURE SCENARIO (Checkpoint 5):
 *
 *   Gateway creates local transaction ABC
 *          -> Gateway sends ABC to cloud
 *          -> Cloud commits PostgreSQL transaction
 *          -> Network connection dies BEFORE gateway receives response
 *          -> Gateway thinks the request failed
 *          -> Outbox retries ABC
 *          -> Cloud recognizes ABC already exists
 *          -> Cloud returns the existing transaction
 *          -> Gateway marks ABC SYNCED
 *
 * Reproduced faithfully, not faked: step 1 below makes a REAL HTTP POST
 * through the REAL HttpCloudClient against the REAL live API - the
 * server genuinely inserts the row and the audit record and commits. The
 * ONLY thing simulated is what a dead network connection would have done
 * anyway: the gateway's own bookkeeping (SQLite) is never told that first
 * attempt succeeded, exactly as if its response had never arrived. Step 2
 * is then the gateway's completely normal, unmodified retry path -
 * SqliteSyncEngine.tick() - using the SAME localIdempotencyKey (nothing
 * about this test regenerates it), which is genuinely indistinguishable,
 * from the cloud's point of view, from a real dropped-connection retry.
 */
describe("Critical failure scenario (mandatory): lost response after cloud commit", () => {
  it("results in exactly ONE PostgreSQL reception and ONE audit record, with the gateway correctly marking it SYNCED", async () => {
    const handle = createTestStorage({
      centreId: BLR_CENTRE_ID,
      cloudApiBaseUrl: CLOUD_TEST_BASE_URL,
      cloudAuthEmail: GATEWAY_BLR_EMAIL,
      cloudAuthPassword: GATEWAY_BLR_PASSWORD,
    });
    await handle.storage.init();

    const input = sampleTransactionInput({ centreId: BLR_CENTRE_ID, sourceId: 1, vehicleId: 1 });
    const { localTransaction, outboxRecord } = await handle.storage.createLocalTransaction(input);

    const cloudClient = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());

    // --- Step 1: the cloud commits, but the gateway "never sees" the response ---
    const firstAttempt = await cloudClient.sendReception({
      localIdempotencyKey: localTransaction.localIdempotencyKey,
      centreId: input.centreId,
      sourceId: input.sourceId,
      vehicleId: input.vehicleId,
      quantityKg: input.quantityKg,
      fat: input.fat,
      snf: input.snf,
      temperature: input.temperature,
    });
    expect(firstAttempt.outcome).toBe("created");
    if (firstAttempt.outcome !== "created") throw new Error("unreachable");
    const cloudTransactionId = firstAttempt.cloudTransactionId;

    // The gateway's own SQLite outbox was never told about firstAttempt -
    // it is still exactly as durable and PENDING as it was before this
    // "lost" delivery, per the checkpoint's core architectural
    // requirement ("cloud unavailable != transaction lost").
    const stillPending = await handle.storage.getOutboxRecordById(outboxRecord.id);
    expect(stillPending?.status).toBe("PENDING");
    const stillUnsynced = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(stillUnsynced?.cloudTransactionId).toBeNull();

    // Exactly one row committed so far.
    expect(countReceptionRowsForKey(localTransaction.localIdempotencyKey)).toBe(1);
    expect(countReceptionCreateAuditEvents(cloudTransactionId)).toBe(1);

    // --- Step 2: the gateway's completely normal retry path ---
    const engine = new SqliteSyncEngine(handle.storage, cloudClient, silentLogger());
    await engine.tick();

    const finalOutbox = await handle.storage.getOutboxRecordById(outboxRecord.id);
    const finalTx = await handle.storage.getLocalTransactionById(localTransaction.id);
    expect(finalOutbox?.status).toBe("SYNCED");
    expect(finalTx?.cloudTransactionId).toBe(cloudTransactionId);

    // The invariant the checkpoint requires: after the retry, there is
    // STILL exactly one PostgreSQL row and exactly one audit record - the
    // retry did not create a second one.
    expect(countReceptionRowsForKey(localTransaction.localIdempotencyKey)).toBe(1);
    expect(countReceptionCreateAuditEvents(cloudTransactionId)).toBe(1);

    await handle.storage.close();
    handle.cleanup();
  });
});
