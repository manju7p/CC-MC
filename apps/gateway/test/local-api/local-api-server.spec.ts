import { createTestStorage, sampleTransactionInput } from "../support/test-storage";
import { LocalApiServer } from "../../src/local-api/local-api-server";
import { HealthService } from "../../src/health/health.service";
import { Logger } from "../../src/logging/logger";
import type { GatewayConfig } from "../../src/config/config.types";

/**
 * Real end-to-end tests of the local (edge) HTTP API - a real Node
 * http.Server bound to 127.0.0.1 on an OS-assigned ephemeral port (port 0),
 * hit with real fetch() calls, backed by a real SqliteLocalStorage (not a
 * mock) - see docs/gateway-architecture.md §17c. This is the surface the
 * frontend's LocalDashboardPage.tsx actually talks to, so it is tested as
 * a real HTTP contract, not just unit-level query logic (already covered
 * by test/storage/local-transactions.spec.ts).
 */
describe("LocalApiServer", () => {
  async function startTestServer(accessToken = "test-token-abc") {
    const handle = createTestStorage();
    await handle.storage.init();

    const config: GatewayConfig = { ...handle.config, localApi: { enabled: true, port: 0, accessToken } };
    const health = new HealthService(config, "0.1.0-test");
    health.setServiceState("RUNNING");
    health.markStarted();
    health.setPendingSyncCount(0);

    const server = new LocalApiServer(config, handle.storage, health, new Logger("test-local-api"));
    await server.start();
    // Reach in to read the OS-assigned port (same "cast past private for
    // test-only introspection" pattern local-transactions.spec.ts already
    // uses for SqliteLocalStorage's own `db`). LocalApiServer doesn't
    // expose this itself - real callers configure a fixed port - but
    // tests need it since we deliberately bind port 0 to avoid collisions
    // between parallel test runs.
    const internalServer = (server as unknown as { server: import("http").Server }).server;
    const address = internalServer.address();
    if (address === null || typeof address === "string") {
      throw new Error("Expected an AddressInfo from a TCP server bound to port 0.");
    }
    const port = address.port;

    return {
      baseUrl: `http://127.0.0.1:${port}`,
      storage: handle.storage,
      server,
      cleanup: async () => {
        await server.stop();
        await handle.storage.close();
        handle.cleanup();
      },
    };
  }

  it("rejects a request with no Authorization header", async () => {
    const t = await startTestServer();
    const res = await fetch(`${t.baseUrl}/local/status`);
    expect(res.status).toBe(401);
    await t.cleanup();
  });

  it("rejects a request with the wrong bearer token", async () => {
    const t = await startTestServer();
    const res = await fetch(`${t.baseUrl}/local/status`, { headers: { Authorization: "Bearer wrong-token" } });
    expect(res.status).toBe(401);
    await t.cleanup();
  });

  it("GET /local/status returns the real health snapshot with a valid token", async () => {
    const t = await startTestServer("right-token");
    const res = await fetch(`${t.baseUrl}/local/status`, { headers: { Authorization: "Bearer right-token" } });
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.serviceState).toBe("RUNNING");
    expect(body.pendingSyncCount).toBe(0);
    expect(body.version).toBe("0.1.0-test");
    await t.cleanup();
  });

  it("GET /local/today reflects a real transaction captured today, and totals it correctly", async () => {
    const t = await startTestServer("right-token");
    const now = new Date().toISOString();
    await t.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: now, quantityKg: 45.5 }));
    await t.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: now, quantityKg: 20 }));

    const res = await fetch(`${t.baseUrl}/local/today`, { headers: { Authorization: "Bearer right-token" } });
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.totalTransactions).toBe(2);
    expect(body.totalQuantityKg).toBeCloseTo(65.5);
    expect(body.transactions).toHaveLength(2);
    // Honest omission - no ACCEPTED/HOLD field should ever appear (see
    // §17b) - only sync state.
    for (const tx of body.transactions) {
      expect(tx.status).toBeUndefined();
      expect(["PENDING", "PROCESSING", "SYNCED", "FAILED"]).toContain(tx.outboxStatus);
    }
    await t.cleanup();
  });

  it("GET /local/today does not include a transaction captured yesterday", async () => {
    const t = await startTestServer("right-token");
    const yesterday = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
    await t.storage.createLocalTransaction(sampleTransactionInput({ capturedAt: yesterday }));

    const res = await fetch(`${t.baseUrl}/local/today`, { headers: { Authorization: "Bearer right-token" } });
    const body = await res.json();
    expect(body.totalTransactions).toBe(0);
    await t.cleanup();
  });

  it("GET /local/transactions?limit=N returns the N most recent, newest first", async () => {
    const t = await startTestServer("right-token");
    for (let i = 0; i < 3; i++) {
      await t.storage.createLocalTransaction(sampleTransactionInput());
    }

    const res = await fetch(`${t.baseUrl}/local/transactions?limit=2`, {
      headers: { Authorization: "Bearer right-token" },
    });
    const body = await res.json();
    expect(body.transactions).toHaveLength(2);
    expect(body.transactions[0].id).toBeGreaterThan(body.transactions[1].id);
    await t.cleanup();
  });

  it("responds to an OPTIONS preflight without requiring auth, with CORS headers set", async () => {
    const t = await startTestServer();
    const res = await fetch(`${t.baseUrl}/local/status`, { method: "OPTIONS" });
    expect(res.status).toBe(204);
    expect(res.headers.get("access-control-allow-origin")).toBe("*");
    await t.cleanup();
  });

  it("returns 404 for an unknown route even with a valid token", async () => {
    const t = await startTestServer("right-token");
    const res = await fetch(`${t.baseUrl}/local/does-not-exist`, { headers: { Authorization: "Bearer right-token" } });
    expect(res.status).toBe(404);
    await t.cleanup();
  });

  it("returns 405 for a non-GET method", async () => {
    const t = await startTestServer("right-token");
    const res = await fetch(`${t.baseUrl}/local/status`, {
      method: "POST",
      headers: { Authorization: "Bearer right-token" },
    });
    expect(res.status).toBe(405);
    await t.cleanup();
  });
});
