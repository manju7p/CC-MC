import type { LocalStorage } from "../storage/storage.types";
import { Logger } from "../logging/logger";
import type { CloudClient, ReceptionSyncPayload } from "./cloud-client.types";
import type { SyncEngine } from "./sync.types";
import type { LocalTransaction, OutboxRecord } from "../storage/local-transaction.types";

export interface SyncEngineOptions {
  /** How often the running loop ticks, in milliseconds. Not used by tick() itself - only by start(). */
  pollIntervalMs: number;
  /** Max outbox rows claimed per tick - keeps a single tick bounded and fast. */
  batchSize: number;
  /** Attempts (including the first) after which a retryable failure is instead marked FAILED (terminal). */
  maxAttempts: number;
  /** Passed to recoverStaleProcessing() on every tick, to catch an in-process hang. See tick()'s doc comment. */
  staleProcessingThresholdMs: number;
}

export const DEFAULT_SYNC_ENGINE_OPTIONS: SyncEngineOptions = {
  pollIntervalMs: 15_000, // 15s - frequent enough to feel responsive, far from "hammering"
  batchSize: 10,
  maxAttempts: 10,
  staleProcessingThresholdMs: 5 * 60_000, // 5 minutes
};

function toPayload(localTransaction: LocalTransaction, localIdempotencyKey: string): ReceptionSyncPayload {
  return {
    localIdempotencyKey,
    centreId: localTransaction.centreId,
    sourceId: localTransaction.sourceId,
    vehicleId: localTransaction.vehicleId,
    quantityKg: localTransaction.quantityKg,
    fat: localTransaction.fat,
    snf: localTransaction.snf,
    temperature: localTransaction.temperature,
  };
}

/**
 * The LOCAL half of the sync engine (Checkpoint 3 scope): claims eligible
 * outbox rows, hands them to an injected CloudClient, and applies the
 * result back to SQLite. Contains no HTTP code at all - CloudClient is
 * the only thing that would ever need to change to go from "talks to a
 * fake" to "talks to the real cloud API" (Checkpoint 5).
 *
 * tick() is the core unit of work and is deliberately exposed as its own
 * public method, callable directly and awaited to completion - this is
 * what makes retry/backoff behavior "deterministic enough to test"
 * (Checkpoint 3 instruction): tests call tick() explicitly and assert on
 * its effects, rather than racing a real setInterval timer. start()/stop()
 * are a thin wrapper around tick() for actual runtime use.
 */
export class SqliteSyncEngine implements SyncEngine {
  private timer: ReturnType<typeof setInterval> | null = null;
  private ticking = false;

  constructor(
    private readonly storage: LocalStorage,
    private readonly cloudClient: CloudClient,
    private readonly logger: Logger = new Logger("sync-engine"),
    private readonly options: SyncEngineOptions = DEFAULT_SYNC_ENGINE_OPTIONS,
    /**
     * Optional, fired once a sync attempt genuinely reaches the cloud and
     * gets back a "created"/"duplicate" outcome (see processOne() below) -
     * an unambiguous, real signal that this gateway just successfully
     * talked to the cloud API. Checkpoint 6D wires this to
     * HealthService.setCloudConnectivity("CONNECTED") in gateway.ts.
     *
     * Deliberately one-directional: there is NO corresponding "mark
     * disconnected" callback here. A "retryable-error" outcome collapses
     * two genuinely different facts into one shape (see
     * cloud-client.types.ts's CloudSendResult doc comment and
     * docs/gateway-architecture.md §14) - "the network is unreachable" vs.
     * "the cloud responded but with a 429/5xx" - and this engine has no
     * safe way to tell them apart without guessing. Reporting CONNECTED on
     * a real success is not a guess; reporting DISCONNECTED on a
     * retryable failure would be, so that direction is intentionally left
     * unwired rather than invented.
     */
    private readonly onCloudConnected?: () => void,
  ) {}

  async start(): Promise<void> {
    if (this.timer) return; // already running - start() is idempotent
    this.timer = setInterval(() => {
      void this.tick();
    }, this.options.pollIntervalMs);
    this.logger.info("Sync engine started", { pollIntervalMs: this.options.pollIntervalMs });
  }

  async stop(): Promise<void> {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
    this.logger.info("Sync engine stopped");
  }

  /**
   * Processes at most one batch of eligible outbox items. Safe to call
   * concurrently with itself (a re-entrant call while one is already in
   * flight is a no-op) - relevant once start() drives this from a real
   * timer, where a slow tick could otherwise overlap the next one.
   */
  async tick(): Promise<void> {
    if (this.ticking) return;
    this.ticking = true;
    try {
      const now = new Date().toISOString();

      // Recover any item that got stuck PROCESSING because a previous
      // tick's delivery attempt hung without ever completing (not the
      // startup case - SqliteLocalStorage.init() already handled that
      // with staleThresholdMs=0; this is the ongoing, same-process
      // safety net using a real staleness threshold).
      const recovered = await this.storage.recoverStaleProcessing(now, this.options.staleProcessingThresholdMs);
      if (recovered > 0) {
        this.logger.warn("Recovered stale PROCESSING outbox rows mid-run", { count: recovered });
      }

      const eligible = await this.storage.findEligibleOutboxItems(now, this.options.batchSize);
      for (const item of eligible) {
        await this.processOne(item);
      }
    } finally {
      this.ticking = false;
    }
  }

  private async processOne(item: OutboxRecord): Promise<void> {
    const claimedAt = new Date().toISOString();
    const claimed = await this.storage.claimOutboxItem(item.id, claimedAt);
    if (!claimed) {
      // Someone else claimed it first (see claimOutboxItem's doc comment
      // on why this is checked even in a single-process gateway).
      return;
    }

    const localTransaction = await this.storage.getLocalTransactionById(item.localTransactionId);
    if (!localTransaction) {
      // Should be unreachable given the FK + atomic-create discipline;
      // if it ever happens, fail this item terminally rather than retry
      // forever against data that doesn't exist.
      await this.storage.markOutboxFailed(item.id, `local_transaction_id=${item.localTransactionId} not found`, new Date().toISOString());
      this.logger.error("Outbox row references a missing local transaction - marked FAILED", { outboxId: item.id });
      return;
    }

    let result;
    try {
      result = await this.cloudClient.sendReception(toPayload(localTransaction, item.localIdempotencyKey));
    } catch (err) {
      // An unexpected thrown error (e.g. the CloudClient itself threw
      // instead of returning a typed result) is treated as retryable -
      // conservative default, since we don't know it's actually terminal.
      await this.handleFailure(item, (err as Error).message);
      return;
    }

    const now = new Date().toISOString();
    switch (result.outcome) {
      case "created":
      case "duplicate":
        await this.storage.markOutboxSynced(item.id, result.cloudTransactionId, now);
        this.logger.info("Outbox item synced", { outboxId: item.id, outcome: result.outcome, cloudTransactionId: result.cloudTransactionId });
        this.onCloudConnected?.();
        break;
      case "retryable-error":
        await this.handleFailure(item, result.message);
        break;
      case "terminal-error":
        await this.storage.markOutboxFailed(item.id, result.message, now);
        this.logger.error("Outbox item permanently failed (terminal cloud error)", { outboxId: item.id, message: result.message });
        break;
    }
  }

  private async handleFailure(item: OutboxRecord, message: string): Promise<void> {
    const now = new Date().toISOString();
    // item.attemptCount is the count BEFORE this failed attempt; the
    // attempt that just failed brings it to attemptCount + 1.
    if (item.attemptCount + 1 >= this.options.maxAttempts) {
      await this.storage.markOutboxFailed(item.id, `${message} (exceeded ${this.options.maxAttempts} attempts)`, now);
      this.logger.error("Outbox item permanently failed (max attempts exceeded)", { outboxId: item.id, attempts: item.attemptCount + 1 });
    } else {
      await this.storage.markOutboxRetry(item.id, message, now);
      this.logger.warn("Outbox item delivery failed, scheduled for retry", { outboxId: item.id, attempt: item.attemptCount + 1 });
    }
  }
}
