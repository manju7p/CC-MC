import type { GatewayConfig } from "./config/config.types";
import { HealthService } from "./health/health.service";
import type { GatewayHealthSnapshot } from "./health/health.types";
import { Logger } from "./logging/logger";
import { DeviceManager } from "./device/device-manager";
import { SqliteLocalStorage } from "./storage/sqlite-local-storage";
import type { LocalStorage } from "./storage/storage.types";
import { HttpCloudClient } from "./sync/http-cloud-client";
import { SqliteSyncEngine } from "./sync/sync-engine";
import type { SyncEngine } from "./sync/sync.types";

/**
 * Orchestrates the gateway process's own lifecycle.
 *
 * Checkpoint 3 added LocalStorage to this lifecycle: start() opens the
 * SQLite database (running migrations and the startup stale-PROCESSING
 * recovery sweep - see SqliteLocalStorage.init()) and stop() closes it
 * cleanly (WAL checkpoint on close - see the persistence proof in
 * sqlite-local-storage.spec.ts). getHealthSnapshot() reports a real
 * pendingSyncCount queried from the outbox.
 *
 * Checkpoint 5 adds the real SyncEngine to this lifecycle: start() now
 * also starts a SqliteSyncEngine backed by a real HttpCloudClient
 * (constructed from config.cloudApiBaseUrl/cloudAuthEmail/cloudAuthPassword),
 * and stop() stops it before closing storage. This was deliberately
 * deferred through Checkpoint 3/4 (see git history) because the only
 * CloudClient that existed then was the fake test double - wiring THAT in
 * would have silently marked real local transactions "synced" without
 * ever reaching the cloud. Now that HttpCloudClient is real, Gateway
 * always constructs a real one by default; a caller (tests, or a future
 * checkpoint) can still inject a different SyncEngine (or LocalStorage)
 * explicitly. SyncEngine itself still knows nothing about HTTP - it talks
 * only to the CloudClient interface (see sync/sync-engine.ts).
 *
 * Checkpoint 6D wires the DEFAULT SqliteSyncEngine's onCloudConnected
 * callback to this.health.setCloudConnectivity("CONNECTED") - the one
 * safe, unambiguous half of the previously-dead cloudConnectivity field
 * (see health.types.ts and docs/gateway-architecture.md §14 for why the
 * other direction, marking DISCONNECTED, is deliberately NOT wired: it
 * would require guessing at an ambiguous error shape). This only applies
 * to the default syncEngine built here - a caller that injects its own
 * SyncEngine (every existing test) gets no such wiring, unchanged.
 */
export class Gateway {
  readonly health: HealthService;
  readonly devices: DeviceManager;
  readonly storage: LocalStorage;
  readonly syncEngine: SyncEngine;
  private readonly logger: Logger;

  constructor(config: GatewayConfig, version: string, logger: Logger = new Logger("gateway"), storage?: LocalStorage, syncEngine?: SyncEngine) {
    this.logger = logger;
    this.health = new HealthService(config, version);
    this.devices = new DeviceManager(this.logger.child("device-manager"));
    this.storage = storage ?? new SqliteLocalStorage(config, logger.child("storage"));
    this.syncEngine =
      syncEngine ??
      new SqliteSyncEngine(
        this.storage,
        new HttpCloudClient(
          config.cloudApiBaseUrl,
          config.cloudAuthEmail,
          config.cloudAuthPassword,
          logger.child("sync-engine.cloud-client"),
        ),
        logger.child("sync-engine"),
        undefined,
        () => this.health.setCloudConnectivity("CONNECTED"),
      );
  }

  /**
   * options.recoverStaleProcessing (default true, forwarded to
   * LocalStorage.init()) - pass { recoverStaleProcessing: false } only
   * when this start() is a one-shot diagnostic call (main.ts's
   * `--status`/`--version` mode) that may run concurrently with an
   * already-running long-lived instance against the same database file.
   * See storage.types.ts's init() doc comment for the full reasoning.
   */
  async start(options?: { recoverStaleProcessing?: boolean }): Promise<void> {
    this.health.setServiceState("STARTING");
    this.logger.info("Gateway starting", { ...this.health.getSnapshot() });

    await this.storage.init(options);
    await this.devices.connectAll();
    await this.syncEngine.start();

    this.health.markStarted();
    this.health.setServiceState("RUNNING");
    await this.refreshPendingSyncCount();
    this.logger.info("Gateway started", { ...this.health.getSnapshot() });
  }

  async stop(): Promise<void> {
    this.health.setServiceState("STOPPING");
    this.logger.info("Gateway stopping", { ...this.health.getSnapshot() });

    await this.syncEngine.stop();
    await this.devices.disconnectAll();
    await this.storage.close();

    this.health.setServiceState("STOPPED");
    this.health.clearStarted();
    this.logger.info("Gateway stopped", { ...this.health.getSnapshot() });
  }

  private async refreshPendingSyncCount(): Promise<void> {
    const count = await this.storage.countPendingOutbox();
    this.health.setPendingSyncCount(count);
  }

  async getHealthSnapshot(): Promise<GatewayHealthSnapshot> {
    if (this.health.getServiceState() === "RUNNING") {
      await this.refreshPendingSyncCount();
    }
    return this.health.getSnapshot();
  }
}
