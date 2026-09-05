namespace CCMC.Domain.ValueObjects;

/// <summary>
/// Normalized milk-analyser reading (BRD v2 Technical Design section 10,
/// minimum fields: FAT, SNF, CLR, Temperature, optional parameters, Timestamp,
/// DeviceId, RawData). CLR and OptionalParameters are nullable/empty by
/// default because no analyser protocol is verified yet - only FAT/SNF/
/// Temperature are validated against quality rules today (see
/// docs/assumptions.md #quality-rule-scope, documented from the cloud API).
/// </summary>
public sealed record MilkQualityReading
{
    public required decimal Fat { get; init; }
    public required decimal Snf { get; init; }
    public decimal? Clr { get; init; }
    public required decimal Temperature { get; init; }
    public IReadOnlyDictionary<string, decimal>? OptionalParameters { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string DeviceId { get; init; }
    public byte[]? RawData { get; init; }
}
