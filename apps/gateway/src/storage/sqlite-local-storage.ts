import * as fs from "fs";
import * as path from "path";
import { DatabaseSync } from "node:sqlite";
import type { GatewayConfig } from "../config/config.types";
import { Logger } from "../logging/logger";
import { runMigrations } from "./schema";
import { computeBackoffDelayMs } from "./backoff";
import type { LocalStorage } from "./storage.types";
import type {
  CreateLocalTransactionInput,
  CreateLocalTransactionResult,
  LocalTransaction,
  OutboxRecord,
  OutboxStatus,
} from "./local-transaction.types";
import { GatewayIdentityMismatchError, IdempotencyKeyConflictError } from "./errors";

export { GatewayIdentityMismatchError, IdempotencyKeyConflictError };

/**
 * Runs fn inside a SQLite transaction. Uses BEGIN IMMEDIATE (not plain
 * BEGIN) to acquire the write lock up front rather than deferring it to
 * the first write statement - the standard SQLite recommendation for
 * write transactions, since it fails fast with SQLITE_BUSY on lock
 * contention instead of potentially deadlocking mid-transaction. This
 * gateway is single-process, so contention is unlikely in practice, but
 * it costs nothing to do this correctly (Rule 11).
 *
 * On any thrown error, explicitly ROLLBACK before re-throwing - this is
 * the mechanism behind the "no partial state" atomicity guarantee (TEST 4).
 */
function withTransaction<T>(db: DatabaseSync, fn: () => T): T {
  db.exec("BEGIN IMMEDIATE");
  try {
    const result = fn();
    db.exec("COMMIT");
    return result;
  } catch (err) {
    db.exec("ROLLBACK");
    throw err;
  }
}

interface LocalTransactionRow {
  id: number;
  local_idempotency_key: string;
  centre_id: number;
  source_id: number;
  vehicle_id: number;
  quantity_kg: number;
  fat: number;
  snf: number;
  temperature: number;
  captured_at: string;
  created_at: string;
  cloud_transaction_id: number | null;
}

interface OutboxRow {
  id: number;
  local_transaction_id: number;
  local_idempotency_key: string;
  status: OutboxStatus;
  attempt_count: number;
  last_attempt_at: string | null;
  next_attempt_at: string;
  last_error: string | null;
  claimed_at: string | null;
  created_at: string;
  updated_at: string;
}

function rowToLocalTransaction(row: LocalTransactionRow): LocalTransaction {
  return {
    id: row.id,
    localIdempotencyKey: row.local_idempotency_key,
    centreId: row.centre_id,
    sourceId: row.source_id,
    vehicleId: row.vehicle_id,
    quantityKg: row.quantity_kg,
    fat: row.fat,
    snf: row.snf,
    temperature: row.temperature,
    capturedAt: row.captured_at,
    createdAt: row.created_at,
    cloudTransactionId: row.cloud_transaction_id,
  };
}

function rowToOutboxRecord(row: OutboxRow): OutboxRecord {
  return {
    id: row.id,
    localTransactionId: row.local_transaction_id,
    localIdempotencyKey: row.local_idempotency_key,
    status: row.status,
    attemptCount: row.attempt_count,
    lastAttemptAt: row.last_attempt_at,
    nextAttemptAt: row.next_attempt_at,
    lastError: row.last_error,
    claimedAt: row.claimed_at,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
  };
}

/**
 * Returns the list of field names whose value differs between an existing
 * local_transactions row and a new create request that reused its
 * localIdempotencyKey. Empty means "this is a genuine retry of the same
 * logical transaction" - safe to treat as a no-op. Non-empty means the
 * caller is reusing the key for different data - see
 * IdempotencyKeyConflictError.
 *
 * Every business field is compared, including capturedAt: a true retry of
 * the same physical capture attempt was assembled from the same readings
 * at the same assembly time (see capture/reception-capture-workflow.ts),
 * so capturedAt should be identical too, not just the "meaningful"
 * numeric fields - comparing it catches a caller that reused a key across
 * two genuinely different capture attempts even if the numbers happened
 * to coincide.
 *
 * Numeric comparison is exact equality (===), not an epsilon/tolerance
 * comparison: the values compared here are the same JSON numbers as
 * received from the caller, not independently-computed floating-point
 * results, so exact equality is the correct and simplest check - a
 * tolerance would risk treating genuinely different readings as "the
 * same" instead.
 */
function findPayloadConflicts(existing: LocalTransactionRow, input: CreateLocalTransactionInput): string[] {
  const conflicts: string[] = [];
  if (existing.centre_id !== input.centreId) conflicts.push("centreId");
  if (existing.source_id !== input.sourceId) conflicts.push("sourceId");
  if (existing.vehicle_id !== input.vehicleId) conflicts.push("vehicleId");
  if (existing.quantity_kg !== input.quantityKg) conflicts.push("quantityKg");
  if (existing.fat !== input.fat) conflicts.push("fat");
  if (existing.snf !== input.snf) conflicts.push("snf");
  if (existing.temperature !== input.temperature) conflicts.push("temperature");
  if (existing.captured_at !== input.capturedAt) conflicts.push("capturedAt");
  return conflicts;
}

/**
 * SQLite-backed LocalStorage, using Node's built-in node:sqlite module
 * (DatabaseSync) rather than an external package such as better-sqlite3.
 *
 * This is a deliberate choice, not the default because "it's what's
 * already installed": better-sqlite3 requires a compiled native addon
 * whose prebuilt binary must match the exact platform/arch/Node-ABI of
 * whatever gets vendored (docs/gateway-decision.md's vendored-runtime
 * approach) - one more moving part to get right for a Windows-only,
 * no-global-Node-install deployment. node:sqlite ships inside Node itself,
 * so there is nothing to prebuild, nothing to match, and zero entries
 * added to package.json's "dependencies" (still {} after this checkpoint)
 * - the most boring option available (Rule 11).
 *
 * Trade-off, stated plainly: node:sqlite is still flagged experimental by
 * Node (its API could change between Node versions). This is an
 * acceptable risk specifically BECAUSE of the vendored-runtime decision -
 * the deployed gateway always runs against one exact, pinned node.exe
 * build (docs/gateway-decision.md §1), so there is no "the user's Node
 * upgraded under us" failure mode the way there would be for a globally-
 * installed runtime. All node:sqlite usage is isolated to this one file
 * behind the LocalStorage interface, so swapping to better-sqlite3 later
 * (e.g. if node:sqlite's API breaks compatibility in some future Node
 * major, or once Windows binary vendoring is actually being tested) is a
 * contained change, not a rewrite.
 *
 * On atomicity vs. a "race recovery" catch: createLocalTransaction() below
 * checks for an existing row by localIdempotencyKey before inserting, and
 * relies on that check plus the UNIQUE constraint - nothing more. It does
 * NOT wrap the inserts in a catch-and-recover-by-re-reading block, even
 * though an earlier version of this method did. That earlier version was
 * wrong: BEGIN IMMEDIATE (see withTransaction()) acquires this
 * connection's write lock before either INSERT runs, and this gateway
 * holds exactly one connection at a time, so there is no window in which
 * another writer could commit a conflicting row between the SELECT and
 * the INSERTs - the "recover from a race" path was solving a problem that
 * cannot occur here. Forcing a genuine mid-transaction failure (see
 * local-transactions.spec.ts's TEST 4) proved it actively wrong: it read
 * back this call's OWN uncommitted insert as if it were a completed prior
 * row and crashed looking for a matching outbox record that did not
 * exist. Letting any insert failure propagate uncaught - so
 * withTransaction's ROLLBACK undoes the whole attempt - is both simpler
 * and correct.
 */
export class SqliteLocalStorage implements LocalStorage {
  private db: DatabaseSync | null = null;
  private readonly dbPath: string;

  constructor(
    private readonly config: GatewayConfig,
    private readonly logger: Logger = new Logger("storage"),
  ) {
    this.dbPath = path.join(config.dataDirectory, "gateway.sqlite");
  }

  private requireDb(): DatabaseSync {
    if (!this.db) {
      throw new Error("SqliteLocalStorage.init() must be called before use.");
    }
    return this.db;
  }

  async init(options?: { recoverStaleProcessing?: boolean }): Promise<void> {
    const shouldRecoverStaleProcessing = options?.recoverStaleProcessing !== false;
    fs.mkdirSync(this.config.dataDirectory, { recursive: true });

    const db = new DatabaseSync(this.dbPath);
    this.db = db;

    try {
      // WAL (Write-Ahead Log) mode + synchronous=FULL: the durability
      // requirement for this checkpoint is explicit ("NO DATA LOSS"). WAL
      // makes crash recovery on next open well-defined (either a WAL frame
      // was fully written and is replayed, or it wasn't and is ignored -
      // never a torn write), and synchronous=FULL fsyncs on every commit
      // rather than trusting the OS write cache - the safest standard
      // combination, at a small write-latency cost this gateway's
      // transaction volume (a handful of receptions per hour, per centre)
      // makes irrelevant.
      db.exec("PRAGMA journal_mode = WAL");
      db.exec("PRAGMA synchronous = FULL");
      // Enforced per-connection by SQLite (not persisted in the file) - must
      // be set on every open, not just the first. Protects the
      // outbox_records -> local_transactions reference from ever pointing
      // at a row that doesn't exist.
      db.exec("PRAGMA foreign_keys = ON");

      runMigrations(db);
      this.initGatewayMetadata(db);

      // Startup recovery sweep: ANY outbox row still PROCESSING at this
      // point was claimed by a PREVIOUS process instance that never got to
      // mark it SYNCED/PENDING/FAILED - this fresh process has made no
      // claims yet, so every such row is, by definition, orphaned by a
      // crash (or an unclean stop). staleThresholdMs=0 means "recover all
      // of them immediately," not "only ones older than some age" - see
      // recoverStaleProcessing()'s doc comment and TEST 2.
      //
      // Skippable via options.recoverStaleProcessing=false - see this
      // method's interface-level doc comment in storage.types.ts for why:
      // the "fresh process has made no claims yet" assumption is only true
      // for a genuinely new long-running instance, not for a `--status`
      // diagnostic call that may run concurrently with an already-running
      // service against the same database file.
      if (shouldRecoverStaleProcessing) {
        const recovered = await this.recoverStaleProcessing(new Date().toISOString(), 0);
        if (recovered > 0) {
          this.logger.warn("Recovered outbox rows stuck PROCESSING from a prior run", { count: recovered });
        }
      }

      this.logger.info("Local storage initialized", { dbPath: this.dbPath });
    } catch (err) {
      // Leave the instance in a clean, retriable state rather than
      // holding an open connection that failed migration/identity
      // validation - a caller catching this error and (say) fixing its
      // config should be able to call init() again without first working
      // around a half-initialized instance.
      db.close();
      this.db = null;
      throw err;
    }
  }

  /**
   * Pins this database file's identity to the gateway/centre it was first
   * initialized for, and fails fast on a mismatch. This exists to catch a
   * real, concrete failure mode: an operator copies a gateway.sqlite file
   * between two different installs (disk clone, manual "restore from
   * backup" onto the wrong machine, etc.). Without this check, the
   * gateway would silently start syncing another centre's outbox rows
   * under this centre's cloud credentials once Checkpoint 5 wires real
   * HTTP - a data-integrity incident, not just a bug. This is exactly the
   * kind of concrete correctness/data-integrity reason Rule Zero asks
   * for, not schema decoration.
   */
  private initGatewayMetadata(db: DatabaseSync): void {
    const existing = db.prepare("SELECT gateway_id, centre_id FROM gateway_metadata WHERE id = 1").get() as
      | { gateway_id: string; centre_id: number }
      | undefined;

    if (!existing) {
      db.prepare("INSERT INTO gateway_metadata (id, gateway_id, centre_id, created_at) VALUES (1, ?, ?, ?)").run(
        this.config.gatewayId,
        this.config.centreId,
        new Date().toISOString(),
      );
      return;
    }

    if (existing.gateway_id !== this.config.gatewayId || existing.centre_id !== this.config.centreId) {
      throw new GatewayIdentityMismatchError(
        `Database at ${this.dbPath} was initialized for gatewayId="${existing.gateway_id}" ` +
          `centreId=${existing.centre_id}, but the loaded config specifies gatewayId="${this.config.gatewayId}" ` +
          `centreId=${this.config.centreId}. Refusing to start: this looks like a gateway.sqlite file ` +
          `copied from a different installation, and starting anyway risks attributing local transactions ` +
          `to the wrong centre. If this data directory is genuinely meant for this gateway/centre, this is ` +
          `a configuration error to fix, not a database error to ignore.`,
      );
    }
  }

  async close(): Promise<void> {
    if (this.db) {
      this.db.close();
      this.db = null;
    }
  }

  async createLocalTransaction(input: CreateLocalTransactionInput): Promise<CreateLocalTransactionResult> {
    const db = this.requireDb();
    const now = new Date().toISOString();

    return withTransaction(db, () => {
      const existingRow = db
        .prepare("SELECT * FROM local_transactions WHERE local_idempotency_key = ?")
        .get(input.localIdempotencyKey) as unknown as LocalTransactionRow | undefined;

      if (existingRow) {
        // Same key, but is this actually a retry of the SAME logical
        // transaction, or a caller reusing this key for different data?
        // Those are not the same situation and must not be handled the
        // same way - see errors.ts's IdempotencyKeyConflictError doc
        // comment for why silently returning the original row on a
        // payload mismatch would be a data-integrity failure, not a
        // convenience.
        const conflicts = findPayloadConflicts(existingRow, input);
        if (conflicts.length > 0) {
          throw new IdempotencyKeyConflictError(input.localIdempotencyKey, conflicts);
        }

        // Idempotent replay (TEST 3): the caller is retrying a create it
        // already succeeded at (or raced with itself) with the SAME data
        // - return the existing pair unchanged rather than inserting
        // again or erroring.
        const outboxRow = db
          .prepare("SELECT * FROM outbox_records WHERE local_transaction_id = ?")
          .get(existingRow.id) as unknown as OutboxRow;
        return {
          localTransaction: rowToLocalTransaction(existingRow),
          outboxRecord: rowToOutboxRecord(outboxRow),
          wasNewlyCreated: false,
        };
      }

      // No try/catch around these two inserts, deliberately - see the
      // class-level doc comment ("On atomicity vs. a 'race recovery'
      // catch"). BEGIN IMMEDIATE (in withTransaction) acquires this
      // connection's write lock before either INSERT runs, and this
      // gateway has exactly one connection open at a time, so no other
      // writer can commit a conflicting row between the SELECT above and
      // these INSERTs - there is no race window here to recover from. If
      // either INSERT throws for any reason (including an unexpected
      // UNIQUE violation on already-inconsistent data), that is a real
      // failure: let it propagate so withTransaction's catch block rolls
      // back the WHOLE transaction, leaving no partial state (TEST 4). An
      // earlier version of this method caught UNIQUE-constraint errors
      // here and tried to "recover" by re-reading a row as if another
      // writer had raced us - that logic was actively wrong (it isn't
      // reachable normally, and when forced, as TEST 4 forces it
      // deliberately, it read back a row from OUR OWN uncommitted insert
      // and crashed trying to find a nonexistent matching outbox row) and
      // was removed in favor of this simpler, correct behavior.
      const insertTx = db.prepare(`
        INSERT INTO local_transactions
          (local_idempotency_key, centre_id, source_id, vehicle_id, quantity_kg, fat, snf, temperature, captured_at, created_at, cloud_transaction_id)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)
      `);
      const txResult = insertTx.run(
        input.localIdempotencyKey,
        input.centreId,
        input.sourceId,
        input.vehicleId,
        input.quantityKg,
        input.fat,
        input.snf,
        input.temperature,
        input.capturedAt,
        now,
      );
      const localTransactionId = Number(txResult.lastInsertRowid);

      const insertOutbox = db.prepare(`
        INSERT INTO outbox_records
          (local_transaction_id, local_idempotency_key, status, attempt_count, last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
        VALUES (?, ?, 'PENDING', 0, NULL, ?, NULL, NULL, ?, ?)
      `);
      const outboxResult = insertOutbox.run(localTransactionId, input.localIdempotencyKey, now, now, now);
      const outboxId = Number(outboxResult.lastInsertRowid);

      const localTransactionRow = db.prepare("SELECT * FROM local_transactions WHERE id = ?").get(localTransactionId) as unknown as LocalTransactionRow;
      const outboxRow = db.prepare("SELECT * FROM outbox_records WHERE id = ?").get(outboxId) as unknown as OutboxRow;

      return {
        localTransaction: rowToLocalTransaction(localTransactionRow),
        outboxRecord: rowToOutboxRecord(outboxRow),
        wasNewlyCreated: true,
      };
    });
  }

  async getLocalTransactionById(id: number): Promise<LocalTransaction | null> {
    const row = this.requireDb().prepare("SELECT * FROM local_transactions WHERE id = ?").get(id) as
      | LocalTransactionRow
      | undefined;
    return row ? rowToLocalTransaction(row) : null;
  }

  async getLocalTransactionByIdempotencyKey(key: string): Promise<LocalTransaction | null> {
    const row = this.requireDb().prepare("SELECT * FROM local_transactions WHERE local_idempotency_key = ?").get(key) as
      | LocalTransactionRow
      | undefined;
    return row ? rowToLocalTransaction(row) : null;
  }

  async listLocalTransactions(): Promise<LocalTransaction[]> {
    const rows = this.requireDb().prepare("SELECT * FROM local_transactions ORDER BY id ASC").all() as unknown as LocalTransactionRow[];
    return rows.map(rowToLocalTransaction);
  }

  async listLocalTransactionsInRange(startIso: string, endIso: string): Promise<LocalTransaction[]> {
    const rows = this.requireDb()
      .prepare("SELECT * FROM local_transactions WHERE captured_at >= ? AND captured_at < ? ORDER BY id ASC")
      .all(startIso, endIso) as unknown as LocalTransactionRow[];
    return rows.map(rowToLocalTransaction);
  }

  async listRecentLocalTransactions(limit: number): Promise<LocalTransaction[]> {
    const rows = this.requireDb()
      .prepare("SELECT * FROM local_transactions ORDER BY id DESC LIMIT ?")
      .all(limit) as unknown as LocalTransactionRow[];
    return rows.map(rowToLocalTransaction);
  }

  async getOutboxRecordById(id: number): Promise<OutboxRecord | null> {
    const row = this.requireDb().prepare("SELECT * FROM outbox_records WHERE id = ?").get(id) as unknown as OutboxRow | undefined;
    return row ? rowToOutboxRecord(row) : null;
  }

  async getOutboxRecordByLocalTransactionId(localTransactionId: number): Promise<OutboxRecord | null> {
    const row = this.requireDb()
      .prepare("SELECT * FROM outbox_records WHERE local_transaction_id = ?")
      .get(localTransactionId) as unknown as OutboxRow | undefined;
    return row ? rowToOutboxRecord(row) : null;
  }

  async listOutboxRecords(): Promise<OutboxRecord[]> {
    const rows = this.requireDb().prepare("SELECT * FROM outbox_records ORDER BY id ASC").all() as unknown as OutboxRow[];
    return rows.map(rowToOutboxRecord);
  }

  async findEligibleOutboxItems(nowIso: string, limit: number): Promise<OutboxRecord[]> {
    const rows = this.requireDb()
      .prepare(
        `SELECT * FROM outbox_records
         WHERE status = 'PENDING' AND next_attempt_at <= ?
         ORDER BY next_attempt_at ASC
         LIMIT ?`,
      )
      .all(nowIso, limit) as unknown as OutboxRow[];
    return rows.map(rowToOutboxRecord);
  }

  async claimOutboxItem(id: number, nowIso: string): Promise<boolean> {
    const result = this.requireDb()
      .prepare("UPDATE outbox_records SET status = 'PROCESSING', claimed_at = ?, updated_at = ? WHERE id = ? AND status = 'PENDING'")
      .run(nowIso, nowIso, id);
    return Number(result.changes) > 0;
  }

  async markOutboxSynced(id: number, cloudTransactionId: number, nowIso: string): Promise<void> {
    const db = this.requireDb();
    withTransaction(db, () => {
      const outbox = db.prepare("SELECT local_transaction_id FROM outbox_records WHERE id = ?").get(id) as
        | { local_transaction_id: number }
        | undefined;
      if (!outbox) {
        throw new Error(`markOutboxSynced: no outbox record with id=${id}`);
      }
      db.prepare("UPDATE outbox_records SET status = 'SYNCED', updated_at = ? WHERE id = ?").run(nowIso, id);
      db.prepare("UPDATE local_transactions SET cloud_transaction_id = ? WHERE id = ?").run(
        cloudTransactionId,
        outbox.local_transaction_id,
      );
    });
  }

  async markOutboxRetry(id: number, errorMessage: string, nowIso: string): Promise<void> {
    const db = this.requireDb();
    const row = db.prepare("SELECT attempt_count FROM outbox_records WHERE id = ?").get(id) as
      | { attempt_count: number }
      | undefined;
    if (!row) {
      throw new Error(`markOutboxRetry: no outbox record with id=${id}`);
    }
    const newAttemptCount = row.attempt_count + 1;
    const nextAttemptAt = new Date(new Date(nowIso).getTime() + computeBackoffDelayMs(newAttemptCount)).toISOString();

    db.prepare(
      `UPDATE outbox_records
       SET status = 'PENDING', attempt_count = ?, last_attempt_at = ?, next_attempt_at = ?, last_error = ?, claimed_at = NULL, updated_at = ?
       WHERE id = ?`,
    ).run(newAttemptCount, nowIso, nextAttemptAt, errorMessage, nowIso, id);
  }

  async markOutboxFailed(id: number, errorMessage: string, nowIso: string): Promise<void> {
    this.requireDb()
      .prepare("UPDATE outbox_records SET status = 'FAILED', last_error = ?, claimed_at = NULL, updated_at = ? WHERE id = ?")
      .run(errorMessage, nowIso, id);
  }

  async recoverStaleProcessing(nowIso: string, staleThresholdMs: number): Promise<number> {
    const cutoff = new Date(new Date(nowIso).getTime() - staleThresholdMs).toISOString();
    const result = this.requireDb()
      .prepare(
        `UPDATE outbox_records
         SET status = 'PENDING', claimed_at = NULL, next_attempt_at = ?, updated_at = ?
         WHERE status = 'PROCESSING' AND (claimed_at IS NULL OR claimed_at <= ?)`,
      )
      .run(nowIso, nowIso, cutoff);
    return Number(result.changes);
  }

  async countPendingOutbox(): Promise<number> {
    const row = this.requireDb()
      .prepare("SELECT COUNT(*) as c FROM outbox_records WHERE status IN ('PENDING', 'PROCESSING')")
      .get() as { c: number };
    return row.c;
  }
}
