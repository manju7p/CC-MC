import type {
  CreateLocalTransactionInput,
  CreateLocalTransactionResult,
  LocalTransaction,
  OutboxRecord,
} from "./local-transaction.types";

/**
 * The gateway's local persistence layer. Implemented by
 * SqliteLocalStorage (Checkpoint 3) - see docs/gateway-architecture.md for
 * the schema and the reasoning behind every table (and the tables
 * deliberately NOT created yet).
 *
 * All methods are async even though the current SQLite implementation
 * (Node's built-in node:sqlite, which is synchronous) doesn't strictly
 * need to be: keeping the interface Promise-based means a future swap to
 * a genuinely async storage engine, or wrapping long-running operations in
 * a worker thread, would not require changing any caller. init()/close()
 * were already async in Checkpoint 2's placeholder for the same reason.
 */
export interface LocalStorage {
  /**
   * Opens the database, runs migrations, validates gateway identity, and
   * (by default) runs the startup stale-PROCESSING recovery sweep - see
   * recoverStaleProcessing()'s doc comment for why that sweep is safe to
   * run unconditionally for a genuinely fresh gateway process.
   *
   * options.recoverStaleProcessing (default true) exists for exactly one
   * caller: main.ts's `--status`/`--version` diagnostic mode, which calls
   * the full Gateway.start()/stop() lifecycle to produce a health
   * snapshot. That path's init() is NOT necessarily "a fresh process that
   * has made no claims yet" - if a real Windows service instance is
   * already running against this same gateway.sqlite file with a
   * genuinely in-flight PROCESSING row, a concurrent `--status` init()
   * would incorrectly requeue that row to PENDING out from under the
   * running service (found during Checkpoint 6A/6B investigation; not
   * previously documented). Passing { recoverStaleProcessing: false }
   * from that one call site skips the sweep without losing the
   * pendingSyncCount signal, since countPendingOutbox() already counts
   * PROCESSING rows alongside PENDING ones. Every other caller (real
   * long-running startup, and every existing test) omits this option and
   * gets the original unconditional-sweep behavior unchanged.
   */
  init(options?: { recoverStaleProcessing?: boolean }): Promise<void>;
  close(): Promise<void>;

  /**
   * Atomically creates a LocalTransaction and its 1:1 OutboxRecord in a
   * single SQLite transaction (BEGIN/COMMIT). If a row with this exact
   * localIdempotencyKey already exists, one of two things happens:
   *
   *  - same key + the SAME payload (every business field, including
   *    capturedAt, identical) -> a genuine retry; returns the existing
   *    pair unchanged (wasNewlyCreated: false), nothing new is written.
   *  - same key + a DIFFERENT payload -> throws
   *    IdempotencyKeyConflictError (errors.ts) and creates nothing. A
   *    caller reusing a key for different data is a bug to surface
   *    loudly, not a case to resolve silently by keeping the original
   *    row - see IdempotencyKeyConflictError's doc comment.
   *
   * See sqlite-local-storage.ts for the full atomicity/idempotency
   * reasoning, and docs/gateway-architecture.md §5 for the design
   * rationale behind the conflict-detection behavior specifically.
   */
  createLocalTransaction(input: CreateLocalTransactionInput): Promise<CreateLocalTransactionResult>;

  getLocalTransactionById(id: number): Promise<LocalTransaction | null>;
  getLocalTransactionByIdempotencyKey(key: string): Promise<LocalTransaction | null>;
  listLocalTransactions(): Promise<LocalTransaction[]>;

  /**
   * LocalTransactions with capturedAt in [startIso, endIso) - added for the
   * local (edge) HTTP API's /local/today endpoint (see
   * src/local-api/local-api-server.ts and docs/gateway-architecture.md's
   * "Local vs. cloud responsibility" section). Filters on capturedAt (when
   * the reading was physically taken), not createdAt (when the row was
   * written locally) - these are usually the same instant for this
   * gateway's own synchronous capture flow, but capturedAt is the field
   * that actually means "today's collection" to an operator. Range is
   * half-open ([start, end)) so a caller can pass one IST calendar day's
   * bounds without double-counting the boundary instant.
   */
  listLocalTransactionsInRange(startIso: string, endIso: string): Promise<LocalTransaction[]>;

  /**
   * The most recently created LocalTransactions, newest first, capped at
   * `limit`. Added for the local HTTP API's /local/transactions endpoint -
   * a recent-activity view for the local dashboard, deliberately not the
   * same query as listLocalTransactions() (which returns everything,
   * oldest-first, and exists for internal/test use, e.g. sync-engine
   * bookkeeping) so a long-running centre's dashboard never has to load
   * its entire transaction history to show "what just happened".
   */
  listRecentLocalTransactions(limit: number): Promise<LocalTransaction[]>;

  getOutboxRecordById(id: number): Promise<OutboxRecord | null>;
  getOutboxRecordByLocalTransactionId(localTransactionId: number): Promise<OutboxRecord | null>;
  listOutboxRecords(): Promise<OutboxRecord[]>;

  /** Outbox rows with status=PENDING and nextAttemptAt <= nowIso, oldest-eligible first. */
  findEligibleOutboxItems(nowIso: string, limit: number): Promise<OutboxRecord[]>;

  /**
   * Atomically transitions one PENDING row to PROCESSING, recording
   * claimedAt. Returns false (no-op) if the row was not PENDING when this
   * ran - defends against a double-claim even though this gateway is
   * currently single-process/single-threaded, so a future change to that
   * assumption doesn't silently reintroduce a race.
   */
  claimOutboxItem(id: number, nowIso: string): Promise<boolean>;

  /** Marks an outbox row SYNCED and stamps the LocalTransaction's cloudTransactionId, atomically. */
  markOutboxSynced(id: number, cloudTransactionId: number, nowIso: string): Promise<void>;

  /** Increments attemptCount, computes the next backoff delay, and returns the row to PENDING. */
  markOutboxRetry(id: number, errorMessage: string, nowIso: string): Promise<void>;

  /** Marks an outbox row FAILED (terminal) - max attempts exceeded or a non-retryable cloud response. */
  markOutboxFailed(id: number, errorMessage: string, nowIso: string): Promise<void>;

  /**
   * Requeues PROCESSING rows whose claim is stale back to PENDING
   * (claimedAt is null, or older than staleThresholdMs before nowIso).
   * Called with staleThresholdMs=0 once at gateway startup (every
   * PROCESSING row found then is necessarily orphaned by a prior crash -
   * a fresh process has made no claims yet) and with a real threshold
   * periodically during the running sync loop (to recover an in-process
   * hang without reclaiming genuinely-in-flight items from THIS process).
   * Returns the number of rows recovered.
   */
  recoverStaleProcessing(nowIso: string, staleThresholdMs: number): Promise<number>;

  /** Count of outbox rows currently PENDING or PROCESSING - feeds HealthService.pendingSyncCount. */
  countPendingOutbox(): Promise<number>;
}
