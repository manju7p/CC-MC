import { CloudAuthTokenCache } from "../../src/sync/cloud-auth";
import { DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS, HttpCloudClient } from "../../src/sync/http-cloud-client";
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
  return `gw-auth-expiry-test:${label}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

/** Calls the REAL /auth/login endpoint directly - mirrors HttpCloudClient's own private login(), duplicated here only because that method isn't exported. */
async function realLogin(): Promise<{ accessToken: string }> {
  const response = await fetch(`${CLOUD_TEST_BASE_URL}/auth/login`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: GATEWAY_BLR_EMAIL, password: GATEWAY_BLR_PASSWORD }),
  });
  if (!response.ok) throw new Error(`login failed with HTTP ${response.status}`);
  const json = (await response.json()) as { accessToken: string };
  return { accessToken: json.accessToken };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * AUTHENTICATION EXPIRY (mandatory, Checkpoint 5): the gateway is
 * unattended and the cloud API's JWT is short-lived
 * (CLOUD_TEST_JWT_EXPIRES_IN = "3s" for this whole cloud-integration
 * suite - see support/live-cloud-fixture.ts - standing in for the real
 * API's 8h default, just fast enough to actually expire within a test).
 *
 * This test forces the REAL reactive "receive 401 -> re-authenticate ->
 * retry safely" path in HttpCloudClient against the REAL live API -
 * not the proactive refresh-before-expiry path (already covered by
 * test/sync/cloud-auth.spec.ts's unit tests). To do that, it hands
 * HttpCloudClient a CloudAuthTokenCache built with an injected clock
 * frozen at the moment of login and a zero refresh margin, so the cache
 * itself never notices real wall-clock time passing and keeps handing
 * out the same now-stale cached token - exactly like a real gateway
 * process that cached a token and then didn't make another cloud call
 * for a while. Only the SERVER'S clock is real, so by the time the
 * request actually goes out, the cloud genuinely rejects the stale token
 * with a real HTTP 401, and HttpCloudClient's own invalidate-and-retry
 * logic (http-cloud-client.ts's `attempt()`) is what has to recover -
 * nothing about that recovery path is mocked or bypassed here.
 */
describe("Authentication expiry (mandatory): real 401 from an expired token triggers re-authentication and a safe, same-key retry", () => {
  it("re-authenticates after a real 401 and completes the SAME logical transaction exactly once", async () => {
    let loginCallCount = 0;
    const trackedLogin = async () => {
      loginCallCount += 1;
      return realLogin();
    };

    // Frozen clock: captured once, never advances - see doc comment above.
    const frozenNowMs = Date.now();
    const tokenCache = new CloudAuthTokenCache(trackedLogin, silentLogger("auth-expiry-test.cache"), () => frozenNowMs, {
      refreshMarginMs: 0,
    });

    // Prime the cache with a real (currently valid) token, real login #1.
    await tokenCache.getToken();
    expect(loginCallCount).toBe(1);

    // Let the REAL token actually expire server-side (CLOUD_TEST_JWT_EXPIRES_IN
    // is 3s). The cache's own frozen clock will still consider it fresh.
    await sleep(3_500);

    const client = new HttpCloudClient(
      CLOUD_TEST_BASE_URL,
      GATEWAY_BLR_EMAIL,
      GATEWAY_BLR_PASSWORD,
      silentLogger("auth-expiry-test.client"),
      DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS,
      fetch,
      tokenCache,
    );

    const key = uniqueKey("expired-token-retry");
    const result = await client.sendReception({
      localIdempotencyKey: key,
      centreId: BLR_CENTRE_ID,
      sourceId: 1,
      vehicleId: 1,
      quantityKg: 555,
      fat: 4.1,
      snf: 8.4,
      temperature: 5.0,
    });

    // The first attempt (with the stale token) got a real 401, was
    // invalidated, and retried ONCE with a freshly-obtained token - the
    // SAME payload object, so the SAME localIdempotencyKey - and that
    // retry succeeded.
    expect(result.outcome).toBe("created");
    expect(loginCallCount).toBe(2); // the priming login, plus exactly one re-auth after the 401

    // Exactly one PostgreSQL row and one audit event - the auth retry did
    // NOT create a second transaction.
    expect(countReceptionRowsForKey(key)).toBe(1);
    if (result.outcome === "created" || result.outcome === "duplicate") {
      expect(countReceptionCreateAuditEvents(result.cloudTransactionId)).toBe(1);
    }
  }, 15_000);
});
