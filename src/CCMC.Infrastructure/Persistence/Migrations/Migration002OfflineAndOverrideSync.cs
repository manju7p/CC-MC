namespace CCMC.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds: (1) offline_credentials - a DPAPI-protected Argon2id verifier +
/// identity/role/centre snapshot per email, enabling offline operator login
/// (see IOfflineCredentialStore); (2) override_outbox - the durable sync
/// queue for manager-override mutations, mirroring outbox_records' state
/// machine (see IOverrideOutboxRepository). Both added by the product
/// decisions recorded in CLAUDE.md "Architecture Decisions".
/// </summary>
internal static class Migration002OfflineAndOverrideSync
{
    public const int Version = 2;
    public const string Name = "OfflineAndOverrideSync";

    public const string Sql = """
        CREATE TABLE offline_credentials (
            email TEXT PRIMARY KEY,
            protected_blob TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE override_outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            override_id INTEGER NOT NULL UNIQUE REFERENCES transaction_overrides (id),
            transaction_local_id INTEGER NOT NULL REFERENCES local_transactions (local_id),
            new_status TEXT NOT NULL,
            reason TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Processing', 'Synced', 'Failed')),
            attempt_count INTEGER NOT NULL DEFAULT 0,
            last_attempt_at TEXT,
            next_attempt_at TEXT NOT NULL,
            last_error TEXT,
            claimed_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX idx_override_outbox_eligibility ON override_outbox (status, next_attempt_at);
        """;
}
