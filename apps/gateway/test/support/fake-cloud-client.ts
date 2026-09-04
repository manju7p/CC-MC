import type { CloudClient, CloudSendResult, ReceptionSyncPayload } from "../../src/sync/cloud-client.types";

/**
 * In-memory CloudClient test double. Per Checkpoint 3's explicit boundary
 * ("Do NOT make real HTTP calls to the cloud in this checkpoint"), this is
 * the ONLY CloudClient implementation that exists right now - it lives
 * under test/, not src/, because it is a test fixture, not shipped
 * gateway code (Checkpoint 5 adds the real one under src/sync/).
 *
 * Supports three ways of scripting behavior, from simplest to most
 * flexible:
 *  - a fixed `behavior` applied to every call
 *  - a `script` array consumed one entry per call to the SAME
 *    localIdempotencyKey (for "fails twice then succeeds" tests)
 *  - real cloud-side idempotency emulation: calling sendReception() twice
 *    with the same key returns "duplicate" the second time with the same
 *    cloudTransactionId, mirroring what the real cloud API is expected to
 *    do once it honors localIdempotencyKey server-side (Checkpoint 5).
 */
export type ScriptedOutcome =
  | { outcome: "created" }
  | { outcome: "duplicate" }
  | { outcome: "retryable-error"; message?: string }
  | { outcome: "terminal-error"; message?: string };

export class FakeCloudClient implements CloudClient {
  private nextCloudTransactionId = 1;
  private readonly seenKeys = new Map<string, number>(); // localIdempotencyKey -> cloudTransactionId
  private readonly scripts = new Map<string, ScriptedOutcome[]>(); // localIdempotencyKey -> queued outcomes
  private readonly calls: ReceptionSyncPayload[] = [];

  constructor(private defaultBehavior: ScriptedOutcome = { outcome: "created" }) {}

  /** Queues outcomes to be consumed in order for calls with this exact key. */
  scriptFor(localIdempotencyKey: string, outcomes: ScriptedOutcome[]): void {
    this.scripts.set(localIdempotencyKey, [...outcomes]);
  }

  setDefaultBehavior(behavior: ScriptedOutcome): void {
    this.defaultBehavior = behavior;
  }

  /** Every payload this client was ever called with, in call order - for asserting call counts/contents. */
  getCalls(): ReceptionSyncPayload[] {
    return [...this.calls];
  }

  callCountFor(localIdempotencyKey: string): number {
    return this.calls.filter((c) => c.localIdempotencyKey === localIdempotencyKey).length;
  }

  async sendReception(payload: ReceptionSyncPayload): Promise<CloudSendResult> {
    this.calls.push(payload);

    // Real cloud-side idempotency emulation takes priority: if this key
    // was already accepted, always return "duplicate" with the same id,
    // regardless of any remaining script - a real cloud endpoint would
    // behave the same way (it doesn't re-run business logic for a key it
    // already has a record of).
    const existingId = this.seenKeys.get(payload.localIdempotencyKey);
    if (existingId !== undefined) {
      return { outcome: "duplicate", cloudTransactionId: existingId };
    }

    const queue = this.scripts.get(payload.localIdempotencyKey);
    const scripted = queue && queue.length > 0 ? queue.shift() : undefined;
    const outcome = scripted ?? this.defaultBehavior;

    switch (outcome.outcome) {
      case "created": {
        const cloudTransactionId = this.nextCloudTransactionId++;
        this.seenKeys.set(payload.localIdempotencyKey, cloudTransactionId);
        return { outcome: "created", cloudTransactionId };
      }
      case "duplicate": {
        // Scripted as "duplicate" without a prior "created" in this fake -
        // simulate the cloud already having a record from before this
        // gateway process even started (e.g. a previous, unseen sync).
        const cloudTransactionId = this.nextCloudTransactionId++;
        this.seenKeys.set(payload.localIdempotencyKey, cloudTransactionId);
        return { outcome: "duplicate", cloudTransactionId };
      }
      case "retryable-error":
        return { outcome: "retryable-error", message: outcome.message ?? "simulated transient failure" };
      case "terminal-error":
        return { outcome: "terminal-error", message: outcome.message ?? "simulated terminal failure" };
    }
  }
}
