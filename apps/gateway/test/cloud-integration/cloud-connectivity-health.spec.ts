import { Gateway } from "../../src/gateway";
import { SqliteSyncEngine } from "../../src/sync/sync-engine";
import { silentLogger, sampleTransactionInput } from "../support/test-storage";
import { BLR_CENTRE_ID, CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD } from "./support/live-cloud-fixture";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";

/**
 * Checkpoint 6D: proves Gateway's DEFAULT SqliteSyncEngine (the one built
 * inside gateway.ts's constructor, not a directly-constructed one, as in
 * sync-engine.spec.ts's unit tests) is actually wired to
 * HealthService.setCloudConnectivity("CONNECTED") on a real successful
 * sync against the real local API + real Postgres - not a mock, and not
 * just asserted via a unit test against the callback in isolation.
 *
 * Deliberately does NOT prove the DISCONNECTED direction - see this
 * checkpoint's report and docs/gateway-architecture.md §14 for why that
 * remains unwired (the existing CloudSendResult shape cannot distinguish
 * "network unreachable" from "cloud responded with an error" without
 * guessing).
 */
describe("Gateway health: cloudConnectivity reflects real sync outcomes (Checkpoint 6D)", () => {
  it("flips cloudConnectivity from UNKNOWN to CONNECTED after the gateway's OWN default SyncEngine completes a real successful sync", async () => {
    const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-cloud-connectivity-"));
    const config = {
      gatewayId: "gw-cloud-connectivity-test",
      centreId: BLR_CENTRE_ID,
      cloudApiBaseUrl: CLOUD_TEST_BASE_URL,
      logDirectory: path.join(tmpDir, "logs"),
      dataDirectory: path.join(tmpDir, "data"),
      cloudAuthEmail: GATEWAY_BLR_EMAIL,
      cloudAuthPassword: GATEWAY_BLR_PASSWORD,
    };

    // No storage/syncEngine override - this is the SAME default
    // construction path main.ts uses, so the onCloudConnected wiring in
    // gateway.ts's constructor is genuinely exercised, not bypassed.
    const gateway = new Gateway(config, "test", silentLogger());
    await gateway.start();

    const before = await gateway.getHealthSnapshot();
    expect(before.cloudConnectivity).toBe("UNKNOWN");

    await gateway.storage.createLocalTransaction(sampleTransactionInput({ centreId: BLR_CENTRE_ID, sourceId: 1, vehicleId: 1 }));

    // Drive one real tick without waiting out the poll interval - tick()
    // is SqliteSyncEngine's own public, directly-callable unit of work
    // (see sync-engine.ts). gateway.syncEngine is typed as the narrower
    // SyncEngine interface (start/stop only); casting to the concrete
    // class to call tick() directly is exactly what main.ts's production
    // interval already does internally, just without the 15s wait.
    await (gateway.syncEngine as SqliteSyncEngine).tick();

    const after = await gateway.getHealthSnapshot();
    expect(after.cloudConnectivity).toBe("CONNECTED");
    expect(after.pendingSyncCount).toBe(0);

    await gateway.stop();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });
});
