import { SqliteLocalStorage, GatewayIdentityMismatchError } from "../../src/storage/sqlite-local-storage";
import { Logger } from "../../src/logging/logger";
import { createTestStorage } from "../support/test-storage";

describe("SqliteLocalStorage - gateway identity pinning", () => {
  it("pins gatewayId/centreId on first init()", async () => {
    const handle = createTestStorage({ gatewayId: "gw-blr-001", centreId: 7 });
    await handle.storage.init();

    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    const row = db.prepare("SELECT gateway_id, centre_id FROM gateway_metadata WHERE id = 1").get() as {
      gateway_id: string;
      centre_id: number;
    };
    expect(row.gateway_id).toBe("gw-blr-001");
    expect(row.centre_id).toBe(7);

    await handle.storage.close();
    handle.cleanup();
  });

  it("succeeds reopening with the SAME gatewayId/centreId (the normal restart case)", async () => {
    const handle = createTestStorage({ gatewayId: "gw-blr-001", centreId: 7 });
    await handle.storage.init();
    await handle.storage.close();

    const second = new SqliteLocalStorage(handle.config, new Logger("test"));
    await expect(second.init()).resolves.toBeUndefined();
    await second.close();

    handle.cleanup();
  });

  it("refuses to start when centreId does not match what the database was initialized for", async () => {
    const handle = createTestStorage({ gatewayId: "gw-blr-001", centreId: 7 });
    await handle.storage.init();
    await handle.storage.close();

    const mismatchedConfig = { ...handle.config, centreId: 999 };
    const second = new SqliteLocalStorage(mismatchedConfig, new Logger("test"));
    await expect(second.init()).rejects.toThrow(GatewayIdentityMismatchError);

    // A fresh instance for the message-content assertion, rather than
    // calling init() a second time on `second` - init() closes its
    // connection and resets to an un-initialized state on failure, so
    // calling it again is valid, but a fresh instance keeps this test
    // from depending on that retry behavior to make its point.
    const third = new SqliteLocalStorage(mismatchedConfig, new Logger("test"));
    await expect(third.init()).rejects.toThrow(/centreId=999/);

    handle.cleanup();
  });

  it("refuses to start when gatewayId does not match what the database was initialized for", async () => {
    const handle = createTestStorage({ gatewayId: "gw-blr-001", centreId: 7 });
    await handle.storage.init();
    await handle.storage.close();

    const mismatchedConfig = { ...handle.config, gatewayId: "gw-different-999" };
    const second = new SqliteLocalStorage(mismatchedConfig, new Logger("test"));
    await expect(second.init()).rejects.toThrow(GatewayIdentityMismatchError);

    handle.cleanup();
  });
});
