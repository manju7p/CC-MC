/**
 * Standalone script (NOT a Jest test file) run as a real child process by
 * subprocess-kill.spec.ts. It opens the gateway's real SqliteLocalStorage
 * against a database path/config passed via argv, begins the exact same
 * atomic create-transaction-plus-outbox flow as
 * SqliteLocalStorage.createLocalTransaction(), but pauses BEFORE COMMIT
 * long enough for the parent test to send SIGKILL to this process - a
 * genuine, real process death mid-transaction, not a simulated/mocked one.
 *
 * Protocol with the parent:
 *  - prints the line "READY_TO_COMMIT" to stdout (and flushes) the moment
 *    both INSERTs have run but BEFORE db.exec("COMMIT") - the parent waits
 *    for exactly this line before sending SIGKILL, so the kill always
 *    lands inside the intended window instead of racing a fixed sleep.
 *  - then busy-waits synchronously for up to 10 seconds (giving the parent
 *    ample time to deliver SIGKILL) so if the kill signal is somehow
 *    delayed or missed, the script still exits on its own rather than
 *    hanging the test suite forever.
 *
 * Uses node:sqlite directly, mirroring (not importing internals from)
 * SqliteLocalStorage's real transaction discipline - BEGIN IMMEDIATE, the
 * same two INSERTs in the same order, no COMMIT reached.
 */
import { DatabaseSync } from "node:sqlite";

const dbPath = process.argv[2];
const key = process.argv[3];

if (!dbPath || !key) {
  process.stderr.write("Usage: kill-before-commit.ts <dbPath> <localIdempotencyKey>\n");
  process.exit(2);
}

const db = new DatabaseSync(dbPath);
db.exec("PRAGMA journal_mode = WAL");
db.exec("PRAGMA synchronous = FULL");
db.exec("PRAGMA foreign_keys = ON");

const now = new Date().toISOString();

db.exec("BEGIN IMMEDIATE");

const txResult = db
  .prepare(
    `INSERT INTO local_transactions
       (local_idempotency_key, centre_id, source_id, vehicle_id, quantity_kg, fat, snf, temperature, captured_at, created_at, cloud_transaction_id)
     VALUES (?, 1, 10, 20, 45.5, 4.2, 8.5, 4.0, ?, ?, NULL)`,
  )
  .run(key, now, now);
const localTransactionId = Number(txResult.lastInsertRowid);

db.prepare(
  `INSERT INTO outbox_records
     (local_transaction_id, local_idempotency_key, status, attempt_count, last_attempt_at, next_attempt_at, last_error, claimed_at, created_at, updated_at)
   VALUES (?, ?, 'PENDING', 0, NULL, ?, NULL, NULL, ?, ?)`,
).run(localTransactionId, key, now, now, now);

// Both inserts are done but NOT committed. Signal the parent, then block
// synchronously (node:sqlite calls are synchronous, so a busy-wait here
// genuinely holds the open transaction rather than yielding to an event
// loop that might commit something else).
process.stdout.write("READY_TO_COMMIT\n");

const deadline = Date.now() + 10_000;
while (Date.now() < deadline) {
  // Busy-wait. Deliberately synchronous/blocking - see header comment.
}

// Only reached if the parent failed to kill us in time - COMMIT so the
// test's failure mode is "unexpectedly running to completion", visible
// in the test's assertions, rather than a silent false pass.
db.exec("COMMIT");
db.close();
