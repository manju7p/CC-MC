import { HttpCloudClient } from "../../src/sync/http-cloud-client";
import { silentLogger } from "../support/test-storage";
import {
  BLR_CENTRE_ID,
  CLOUD_TEST_BASE_URL,
  GATEWAY_BLR_EMAIL,
  GATEWAY_BLR_PASSWORD,
  countReceptionCreateAuditEvents,
  countReceptionRowsForKey,
} from "./support/live-cloud-fixture";

function uniqueKey(label: string): string {
  return `gw-concurrent-test:${label}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

const BASE_PAYLOAD = {
  centreId: BLR_CENTRE_ID,
  sourceId: 1,
  vehicleId: 1,
  quantityKg: 640,
  fat: 4.4,
  snf: 8.6,
  temperature: 5.0,
};

/**
 * The mandatory concurrent-duplicate test: two REAL, genuinely
 * simultaneous HTTP requests (two independent HttpCloudClient instances,
 * each with its own login/connection - not a single client called
 * twice) racing to deliver the same localIdempotencyKey against the real
 * live API/Postgres.
 */
describe("Concurrent duplicate test (mandatory): two simultaneous requests, same localIdempotencyKey", () => {
  it("same key + same payload: exactly ONE reception, ONE transaction number, ONE creation audit event", async () => {
    const key = uniqueKey("same-payload");
    const clientA = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());
    const clientB = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());

    const [resultA, resultB] = await Promise.all([
      clientA.sendReception({ ...BASE_PAYLOAD, localIdempotencyKey: key }),
      clientB.sendReception({ ...BASE_PAYLOAD, localIdempotencyKey: key }),
    ]);

    if (resultA.outcome !== "created" && resultA.outcome !== "duplicate") throw new Error(`unexpected outcome: ${resultA.outcome}`);
    if (resultB.outcome !== "created" && resultB.outcome !== "duplicate") throw new Error(`unexpected outcome: ${resultB.outcome}`);
    // Never both "created" - exactly one of the two actually inserted the row.
    const outcomes = [resultA.outcome, resultB.outcome].sort();
    expect(outcomes).toEqual(["created", "duplicate"]);

    expect(resultA.cloudTransactionId).toBe(resultB.cloudTransactionId);

    expect(countReceptionRowsForKey(key)).toBe(1);
    expect(countReceptionCreateAuditEvents(resultA.cloudTransactionId)).toBe(1);
  });

  it("same key + DIFFERENT payload: exactly ONE transaction created; the loser gets an explicit conflict, not a silent success", async () => {
    const key = uniqueKey("conflicting-payload");
    const clientA = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());
    const clientB = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());

    const [resultA, resultB] = await Promise.all([
      clientA.sendReception({ ...BASE_PAYLOAD, quantityKg: 111, localIdempotencyKey: key }),
      clientB.sendReception({ ...BASE_PAYLOAD, quantityKg: 222, localIdempotencyKey: key }),
    ]);

    const outcomes = [resultA.outcome, resultB.outcome].sort();
    // Exactly one succeeds (as "created" - it cannot be "duplicate" of
    // itself since the payloads differ); the other MUST see a payload
    // conflict (terminal-error), never a silent "duplicate" success with
    // the wrong data quietly discarded.
    expect(outcomes).toEqual(["created", "terminal-error"]);

    const winner = resultA.outcome === "created" ? resultA : resultB;
    const loser = resultA.outcome === "terminal-error" ? resultA : resultB;
    if (winner.outcome !== "created") throw new Error("unreachable");
    if (loser.outcome !== "terminal-error") throw new Error("unreachable");
    expect(loser.message).toMatch(/conflict/i);

    expect(countReceptionRowsForKey(key)).toBe(1);
    expect(countReceptionCreateAuditEvents(winner.cloudTransactionId)).toBe(1);
  });

  it("ten simultaneous requests with the same key and payload still produce exactly ONE row", async () => {
    const key = uniqueKey("ten-way-race");
    const clients = Array.from(
      { length: 10 },
      () => new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger()),
    );

    const results = await Promise.all(clients.map((c) => c.sendReception({ ...BASE_PAYLOAD, localIdempotencyKey: key })));

    const createdCount = results.filter((r) => r.outcome === "created").length;
    const duplicateCount = results.filter((r) => r.outcome === "duplicate").length;
    expect(createdCount).toBe(1);
    expect(duplicateCount).toBe(9);

    expect(countReceptionRowsForKey(key)).toBe(1);
  });
});
