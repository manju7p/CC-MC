namespace CCMC.Infrastructure.Persistence.Migrations;

/// <summary>
/// The initial local schema, designed to be logically compatible with the
/// cloud's own schema (documented in context.md "Database Responsibilities")
/// plus local-only infrastructure tables (device_configurations,
/// outbox_records) per BRD v2 section 14. Column names use snake_case
/// (SQLite convention) regardless of the C# entity's PascalCase - repositories
/// own that mapping.
/// </summary>
internal static class Migration001InitialSchema
{
    public const int Version = 1;
    public const string Name = "InitialSchema";

    public const string Sql = """
        CREATE TABLE chilling_centres (
            id INTEGER PRIMARY KEY,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            status TEXT NOT NULL
        );

        CREATE TABLE sources (
            id INTEGER PRIMARY KEY,
            code TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            location TEXT,
            contact TEXT,
            milk_type TEXT,
            status TEXT NOT NULL,
            centre_id INTEGER NOT NULL
        );

        CREATE TABLE vehicles (
            id INTEGER PRIMARY KEY,
            vehicle_number TEXT NOT NULL UNIQUE,
            tanker_number TEXT,
            driver_name TEXT,
            driver_mobile TEXT,
            capacity_kg REAL,
            status TEXT NOT NULL,
            centre_id INTEGER NOT NULL
        );

        CREATE TABLE quality_rules (
            id INTEGER PRIMARY KEY,
            parameter TEXT NOT NULL,
            min_value REAL NOT NULL,
            max_value REAL NOT NULL,
            centre_id INTEGER,
            UNIQUE (parameter, centre_id)
        );

        CREATE TABLE device_configurations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            com_port TEXT NOT NULL,
            baud_rate INTEGER NOT NULL,
            data_bits INTEGER NOT NULL,
            stop_bits TEXT NOT NULL,
            parity TEXT NOT NULL,
            flow_control TEXT NOT NULL,
            read_timeout_ms INTEGER NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE local_transactions (
            local_id INTEGER PRIMARY KEY AUTOINCREMENT,
            transaction_number TEXT,
            centre_id INTEGER NOT NULL,
            source_id INTEGER NOT NULL,
            vehicle_id INTEGER NOT NULL,
            operator_user_id INTEGER NOT NULL,
            quantity_kg REAL NOT NULL,
            fat REAL NOT NULL,
            snf REAL NOT NULL,
            temperature REAL NOT NULL,
            status TEXT NOT NULL,
            reading_source TEXT NOT NULL,
            reason TEXT,
            local_idempotency_key TEXT NOT NULL UNIQUE,
            captured_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            cloud_transaction_id INTEGER
        );
        CREATE INDEX idx_local_transactions_centre_captured ON local_transactions (centre_id, captured_at);

        CREATE TABLE transaction_overrides (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            transaction_local_id INTEGER NOT NULL REFERENCES local_transactions (local_id),
            original_status TEXT NOT NULL,
            new_status TEXT NOT NULL,
            performed_by_user_id INTEGER NOT NULL,
            reason TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE outbox_records (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            transaction_local_id INTEGER NOT NULL UNIQUE REFERENCES local_transactions (local_id),
            local_idempotency_key TEXT NOT NULL UNIQUE,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Processing', 'Synced', 'Failed')),
            attempt_count INTEGER NOT NULL DEFAULT 0,
            last_attempt_at TEXT,
            next_attempt_at TEXT NOT NULL,
            last_error TEXT,
            claimed_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX idx_outbox_eligibility ON outbox_records (status, next_attempt_at);

        CREATE TABLE audit_log_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id INTEGER,
            centre_id INTEGER,
            action TEXT NOT NULL,
            resource_type TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            old_value_json TEXT,
            new_value_json TEXT,
            reason TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX idx_audit_user_created ON audit_log_entries (user_id, created_at);
        """;
}
