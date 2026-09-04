/**
 * The gateway's abstraction over "send one reception to the cloud API."
 * No implementation of this interface exists yet - Checkpoint 5 adds a
 * real HTTP client wired to apps/api's POST /reception endpoint. This
 * checkpoint only defines the contract and exercises it against a
 * fake/in-memory implementation (test/support/fake-cloud-client.ts) so
 * SyncEngine's retry/backoff/state-machine logic can be proven without
 * making any real network call, per the explicit Checkpoint 3 boundary.
 *
 * The shape mirrors apps/api/src/reception/dto/create-reception.dto.ts
 * plus localIdempotencyKey - the field the real cloud endpoint will need
 * to add in Checkpoint 5 to actually implement dedup server-side (the
 * cloud schema already reserves MilkReceptionTransaction.localIdempotencyKey
 * for exactly this - see docs/assumptions.md #transaction-numbering).
 * Nothing about the existing cloud API is changed in this checkpoint.
 */
export interface ReceptionSyncPayload {
  localIdempotencyKey: string;
  centreId: number;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
}

/**
 * Discriminated union modeling the real cloud API's eventual behavior:
 *
 *  - "created": the cloud accepted this as a brand-new transaction.
 *  - "duplicate": the cloud recognized localIdempotencyKey as one it had
 *    already processed, and returned the EXISTING transaction rather than
 *    creating a second one - this is the server-side half of the
 *    idempotency guarantee (the gateway-side half is
 *    LocalStorage.createLocalTransaction's dedup-by-key). Both "created"
 *    and "duplicate" are successes from the sync engine's point of view:
 *    either way, the cloud now has exactly one transaction for this key.
 *  - "retryable-error": a transient failure (network timeout, 5xx, cloud
 *    temporarily unavailable) - safe and expected to retry with backoff.
 *  - "terminal-error": a failure that will never succeed by retrying
 *    unchanged (e.g. a validation error) - retrying would just waste
 *    attempts, so this goes straight to FAILED for manual/ops review.
 */
export type CloudSendResult =
  | { outcome: "created"; cloudTransactionId: number }
  | { outcome: "duplicate"; cloudTransactionId: number }
  | { outcome: "retryable-error"; message: string }
  | { outcome: "terminal-error"; message: string };

export interface CloudClient {
  sendReception(payload: ReceptionSyncPayload): Promise<CloudSendResult>;
}
