import type { DatabaseSync } from "node:sqlite";

/**
 * A minimal migration framework - real, ordered, tracked migrations
 * rather than a single ad-hoc "CREATE TABLE IF NOT EXISTS" blob run on
 * every start. Each migration is applied at most once (tracked in
 * schema_migrations) and applying the full set is idempotent: running it
 * against a brand-new database or an already-migrated one both leave the
 * database in the same state.
 *
 * Schema versioning matters here specifically because this database's
 * *format* will change again (Checkpoint 4+ may add tables once real
 * device/simulator requirements are known - see
 * docs/gateway-architecture.md's "deliberately deferred" section) and a
 * gateway upgrade must not require a human to hand-edit a deployed
 * SQLite file.
 */
export interface Migration {
  version: number;
  name: string;
  up: (db: DatabaseSync) => void;
}

const MIGRATIONS: Migration[] = [
  {
    version: 1,
    name: "initial_schema",
    up(db) {
      // gateway_metadata: single-row identity pin for this database file.
      // See sqlite-local-storage.ts's initGatewayMetadata() for why this
      // exists - it is a concrete data-integrity safeguard (catches a
      // SQLite file copied between two different gateway installs), not
      // a duplicate of the JSON config file.
      db.exec(`
        CREATE TABLE gateway_metadata (
          id INTEGER PRIMARY KEY CHECK (id = 1),
          gateway_id TEXT NOT NULL,
          centre_id INTEGER NOT NULL,
          created_at TEXT NOT NULL
        );
      `);

      // local_transactions: the local mirror of an assembled-but-not-yet-
      // cloud-acknowledged milk reception. Shape mirrors CreateReceptionDto
      // deliberately - see local-transaction.types.ts's header comment.
      db.exec(`
        CREATE TABLE local_transactions (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          local_idempotency_key TEXT NOT NULL UNIQUE,
          centre_id INTEGER NOT NULL,
          source_id INTEGER NOT NULL,
          vehicle_id INTEGER NOT NULL,
          quantity_kg REAL NOT NULL,
          fat REAL NOT NULL,
          snf REAL NOT NULL,
          temperature REAL NOT NULL,
          captured_at TEXT NOT NULL,
          created_at TEXT NOT NULL,
          cloud_transaction_id INTEGER NULL
        );
      `);

      // outbox_records: one row per local_transaction (1:1, enforced by
      // the UNIQUE on local_transaction_id), tracking sync ATTEMPT state.
      // local_idempotency_key is intentionally denormalized (copied) from
      // local_transactions rather than requiring a join - see
      // sqlite-local-storage.ts for why this is safe (both rows are
      // written in the same atomic transaction and neither is ever
      // updated after creation).
      db.exec(`
        CREATE TABLE outbox_records (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          local_transaction_id INTEGER NOT NULL UNIQUE
            REFERENCES local_transactions(id),
          local_idempotency_key TEXT NOT NULL UNIQUE,
          status TEXT NOT NULL CHECK (status IN ('PENDING','PROCESSING','SYNCED','FAILED')),
          attempt_count INTEGER NOT NULL DEFAULT 0,
          last_attempt_at TEXT NULL,
          next_attempt_at TEXT NOT NULL,
          last_error TEXT NULL,
          claimed_at TEXT NULL,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        );
      `);

      // Hot path: "find PENDING rows eligible for a retry now", ordered by
      // due time. This is the index the sync engine's every tick depends on.
      db.exec(`
        CREATE INDEX idx_outbox_eligibility
          ON outbox_records (status, next_attempt_at);
      `);
    },
  },
];

/**
 * Applies every migration with version greater than the database's
 * current recorded version, each inside its own SQLite transaction so a
 * failed migration never leaves the schema half-applied. Bootstraps the
 * schema_migrations table itself first if absent (that one table is
 * created outside the numbered-migration list, since migrations need it
 * to exist before they can be tracked).
 */
export function runMigrations(db: DatabaseSync): void {
  db.exec(`
    CREATE TABLE IF NOT EXISTS schema_migrations (
      version INTEGER PRIMARY KEY,
      name TEXT NOT NULL,
      applied_at TEXT NOT NULL
    );
  `);

  const appliedRow = db.prepare("SELECT COALESCE(MAX(version), 0) as maxVersion FROM schema_migrations").get() as {
    maxVersion: number;
  };
  const currentVersion = appliedRow.maxVersion;

  const pending = MIGRATIONS.filter((m) => m.version > currentVersion).sort((a, b) => a.version - b.version);

  for (const migration of pending) {
    db.exec("BEGIN IMMEDIATE");
    try {
      migration.up(db);
      db.prepare("INSERT INTO schema_migrations (version, name, applied_at) VALUES (?, ?, ?)").run(
        migration.version,
        migration.name,
        new Date().toISOString(),
      );
      db.exec("COMMIT");
    } catch (err) {
      db.exec("ROLLBACK");
      throw new Error(`Migration ${migration.version} ("${migration.name}") failed and was rolled back: ${(err as Error).message}`);
    }
  }
}
