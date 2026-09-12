namespace CCMC.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the milk analyser's additional fields (density/CLR, added-water
/// percent, protein, and the raw payload they were decoded from) to
/// local_transactions - additive/nullable so every existing row (captured
/// before this migration, or captured without an analyser reading at all)
/// simply has NULL for all four (see MilkReceptionTransaction's doc comment).
/// SQLite's ALTER TABLE ... ADD COLUMN cannot add a UNIQUE/CHECK constraint,
/// which none of these need.
/// </summary>
internal static class Migration003AnalyserFields
{
    public const int Version = 3;
    public const string Name = "AnalyserFields";

    public const string Sql = """
        ALTER TABLE local_transactions ADD COLUMN clr NUMERIC;
        ALTER TABLE local_transactions ADD COLUMN water NUMERIC;
        ALTER TABLE local_transactions ADD COLUMN protein NUMERIC;
        ALTER TABLE local_transactions ADD COLUMN raw_analyser_payload TEXT;
        """;
}
