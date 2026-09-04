/**
 * A LocalTransaction is the gateway's local record of a completed milk
 * reception, captured at this centre, not yet acknowledged by the cloud.
 * Its shape mirrors the cloud's CreateReceptionDto (apps/api/src/reception/
 * dto/create-reception.dto.ts) deliberately - it is the local mirror of
 * what will eventually become a MilkReceptionTransaction row once synced
 * (Checkpoint 5), not a new invented shape.
 *
 * This is intentionally the *assembled reception* level, not raw
 * scale/analyser readings (ScaleReading/AnalyserReading from
 * normalization/normalized-reading.types.ts). Persisting raw readings as
 * their own rows has no consumer yet - nothing in this checkpoint or the
 * next reads them back - so it is not built now (Rule 4: don't build what
 * isn't needed yet). The BRD's actual transactional/sync unit is the
 * completed reception, which is what needs outbox/retry/idempotency
 * treatment; Checkpoint 4's simulator-driven capture flow is what will
 * decide how raw readings get assembled into a LocalTransaction, and can
 * revisit this if a concrete need for raw-reading persistence appears.
 */
export interface LocalTransaction {
  id: number;

  /**
   * Stable, caller-generated key (see idempotency.ts's
   * generateLocalIdempotencyKey()). Never regenerated for a given logical
   * transaction, including across retries of the persistence attempt
   * itself - see storage.reasoning in sqlite-local-storage.ts.
   */
  localIdempotencyKey: string;

  centreId: number;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;

  /** When the underlying reading(s) were physically captured, ISO-8601. */
  capturedAt: string;

  /** When this row was written to local SQLite, ISO-8601. */
  createdAt: string;

  /**
   * The cloud-assigned MilkReceptionTransaction.id, once sync succeeds.
   * NULL means "not yet synced" - this column IS the synced/unsynced
   * signal; outbox_records.status is a separate concept (sync ATTEMPT
   * state, not "has this been synced") precisely so the two can never
   * silently disagree about "is this synced" - see
   * docs/gateway-architecture.md's outbox design section.
   */
  cloudTransactionId: number | null;
}

export interface CreateLocalTransactionInput {
  localIdempotencyKey: string;
  centreId: number;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
  capturedAt: string;
}

export interface CreateLocalTransactionResult {
  localTransaction: LocalTransaction;
  outboxRecord: OutboxRecord;
  /**
   * false when localIdempotencyKey already existed and this call was a
   * safe no-op replay (TEST 3: duplicate-create must not create a
   * duplicate row) rather than a genuinely new insert.
   */
  wasNewlyCreated: boolean;
}

/**
 * Outbox record state machine - see docs/gateway-architecture.md for the
 * full state-transition diagram and reasoning. Summary:
 *
 *   PENDING    - eligible for a sync attempt once now >= nextAttemptAt.
 *                Initial state, and the state retried items return to.
 *   PROCESSING - claimed by a sync engine tick, delivery in flight.
 *                NEVER a terminal state - see recoverStaleProcessing().
 *   SYNCED     - cloud confirmed receipt (created or duplicate-detected).
 *                Terminal, success.
 *   FAILED     - exceeded max retry attempts, or the cloud reported a
 *                terminal (non-retryable) error. Terminal, requires
 *                manual/ops intervention - rows are never deleted, so
 *                nothing is silently lost (see the "gateway is an edge
 *                system" requirement).
 */
export type OutboxStatus = "PENDING" | "PROCESSING" | "SYNCED" | "FAILED";

export interface OutboxRecord {
  id: number;
  localTransactionId: number;
  localIdempotencyKey: string;
  status: OutboxStatus;
  attemptCount: number;
  lastAttemptAt: string | null;
  nextAttemptAt: string;
  lastError: string | null;
  claimedAt: string | null;
  createdAt: string;
  updatedAt: string;
}
