import { HttpCloudClient } from "../../src/sync/http-cloud-client";
import { silentLogger } from "../support/test-storage";
import {
  BLR_CENTRE_ID,
  CLOUD_TEST_BASE_URL,
  GATEWAY_BLR_EMAIL,
  GATEWAY_BLR_PASSWORD,
  GATEWAY_MYS_EMAIL,
  GATEWAY_MYS_PASSWORD,
  MYS_CENTRE_ID,
  countReceptionRowsForKey,
} from "./support/live-cloud-fixture";

function uniqueKey(label: string): string {
  return `gw-centre-security-test:${label}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * CENTRE SECURITY (Checkpoint 5): the gateway must never be able to
 * bypass centre scoping. Each gateway service-account is associated with
 * exactly one centre (apps/api/src/seed.ts's UserCentreAssignment rows -
 * never allCentres), and CentreAccessService.assertCanAccess() - the SAME
 * mechanism any human user's request goes through, no parallel gateway
 * auth path - is what actually enforces this. Proven here through the
 * REAL gateway HttpCloudClient against the REAL live API, not just at the
 * raw HTTP/API layer (see apps/api/test/reception-idempotency.e2e-spec.ts
 * for that complementary coverage).
 */
describe("Centre security (mandatory): a gateway for Centre A cannot create or manipulate Centre B records", () => {
  it("Bangalore gateway sending a reception for Mysore's centreId is rejected, and creates nothing", async () => {
    const key = uniqueKey("blr-to-mys");
    const client = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());

    const result = await client.sendReception({
      localIdempotencyKey: key,
      centreId: MYS_CENTRE_ID, // NOT the Bangalore gateway's own centre
      sourceId: 1,
      vehicleId: 1,
      quantityKg: 500,
      fat: 4.0,
      snf: 8.5,
      temperature: 5.0,
    });

    expect(result.outcome).toBe("terminal-error");
    if (result.outcome !== "terminal-error") throw new Error("unreachable");
    // Not retryable - retrying the same cross-centre request would be
    // rejected forever, so it must not go back into the outbox loop.
    expect(result.message.toLowerCase()).toMatch(/no access|forbidden|centre/);

    // Nothing was created cloud-side - the rejection happened before any
    // row was ever inserted.
    expect(countReceptionRowsForKey(key)).toBe(0);
  });

  it("Mysore gateway sending a reception for Bangalore's centreId is rejected, and creates nothing", async () => {
    const key = uniqueKey("mys-to-blr");
    const client = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_MYS_EMAIL, GATEWAY_MYS_PASSWORD, silentLogger());

    const result = await client.sendReception({
      localIdempotencyKey: key,
      centreId: BLR_CENTRE_ID, // NOT the Mysore gateway's own centre
      sourceId: 1,
      vehicleId: 1,
      quantityKg: 500,
      fat: 4.0,
      snf: 8.5,
      temperature: 5.0,
    });

    expect(result.outcome).toBe("terminal-error");
    if (result.outcome !== "terminal-error") throw new Error("unreachable");
    expect(result.message.toLowerCase()).toMatch(/no access|forbidden|centre/);

    expect(countReceptionRowsForKey(key)).toBe(0);
  });

  it("each gateway CAN still create a reception for its own centre (proves the rejection above is centre-scoping, not a broken client)", async () => {
    const key = uniqueKey("blr-own-centre-sanity");
    const client = new HttpCloudClient(CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD, silentLogger());

    const result = await client.sendReception({
      localIdempotencyKey: key,
      centreId: BLR_CENTRE_ID,
      sourceId: 1,
      vehicleId: 1,
      quantityKg: 500,
      fat: 4.0,
      snf: 8.5,
      temperature: 5.0,
    });

    expect(result.outcome).toBe("created");
    expect(countReceptionRowsForKey(key)).toBe(1);
  });
});
