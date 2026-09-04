/**
 * Standalone script (NOT a Jest test file), companion to
 * kill-before-commit.ts - see that file's header comment for the general
 * pattern. This one proves the OTHER real-crash scenario (TEST 2): a
 * transaction was fully, successfully committed (both local_transaction
 * and outbox row exist), the outbox row was claimed (PROCESSING), and
 * THEN the process dies before it could mark the item SYNCED/PENDING/
 * FAILED - simulating a real crash during cloud delivery.
 *
 * Protocol with the parent: prints "CLAIMED" once the claim UPDATE has
 * been committed, then busy-waits for the parent's SIGKILL (with the same
 * 10-second self-exit safety net as kill-before-commit.ts).
 */
import { DatabaseSync } from "node:sqlite";

const dbPath = process.argv[2];
const key = process.argv[3];

if (!dbPath || !key) {
  process.stderr.write("Usage: kill-during-processing.ts <dbPath> <localIdempotencyKey>\n");
  process.exit(2);
}

const db = new DatabaseSync(dbPath);
db.exec("PRAGMA journal_mode = WAL");
db.exec("PRAGMA synchronous = FULL");
db.exec("PRAGMA foreign_keys = ON");

const now = new Date().toISOString();

// Full, committed create - both rows genuinely exist after this.
db.exec("BEGIN IMMEDIATE");
const txResult = db
  .prepare(
    `INSERT INTO local_transactions
       (local_idempotency_key, centre_id, source_id, vehicle_id, quantity_kg, fat, snf, temperature, captured_at, created_at, cloud_transaction_id)
     VALUES (?, 1, 10, 20, 45.5, 4.2, 8.5, 4.0, ?, ?, NULL)`,
  )
  .run(key, now, now);
const localTransactionId = Number(txResult.lastInsertRowid);
const outboxResult = db
  .prepare(
    `INSERT INTO outbox_records
       (local_transaction_id, local_idempotency_key, status, attempt_count, last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
     VALUES (?, ?, 'PENDING', 0, NULL, ?, NULL, NULL, ?, ?)`,
  )
  .run(localTransactionId, key, now, now, now);
db.exec("COMMIT");
const outboxId = Number(outboxResult.lastInsertRowid);

// Claim it (mirrors SqliteLocalStorage.claimOutboxItem) - this commits too,
// so the PROCESSING state genuinely persists to disk before we die.
db.prepare("UPDATE outbox_records SET status = 'PROCESSING', claimed_at = ?, updated_at = ? WHERE id = ?").run(
  now,
  now,
  outboxId,
);

process.stdout.write("CLAIMED\n");

// Now simulate the delivery attempt hanging forever (e.g. a network call
// that never resolves) - the process is killed while "in flight", having
// never called markOutboxSynced/markOutboxRetry/markOutboxFailed.
const deadline = Date.now() + 10_000;
while (Date.now() < deadline) {
  // Busy-wait - see kill-before-commit.ts's header comment.
}

db.close();
