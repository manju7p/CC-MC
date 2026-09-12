namespace CCMC.Infrastructure.Persistence.Migrations;

/// <summary>
/// BRD v5.0 section 25 Rate Calculation. Adds rate/amount to local_transactions
/// (additive/nullable, same reasoning as Migration003's analyser fields - a
/// reception saved before this migration simply has NULL for both, distinct
/// from a reception where the calculation legitimately produced 0) and a new
/// rate_formula_settings table mirroring quality_rules' centre-specific-over-
/// global shape (null centre_id = global default).
/// </summary>
internal static class Migration004RateCalculation
{
    public const int Version = 4;
    public const string Name = "RateCalculation";

    public const string Sql = """
        ALTER TABLE local_transactions ADD COLUMN rate NUMERIC;
        ALTER TABLE local_transactions ADD COLUMN amount NUMERIC;

        CREATE TABLE rate_formula_settings (
            id INTEGER PRIMARY KEY,
            rate_type TEXT NOT NULL,
            value1 REAL,
            value2 REAL,
            ts_rate REAL,
            centre_id INTEGER,
            UNIQUE (centre_id)
        );
        """;
}
