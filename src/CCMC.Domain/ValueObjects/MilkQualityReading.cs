namespace CCMC.Domain.ValueObjects;

/// <summary>
/// Normalized milk-analyser reading (BRD v2 Technical Design section 10,
/// minimum fields: FAT, SNF, CLR, Temperature, optional parameters, Timestamp,
/// DeviceId, RawData). Now that a real protocol IS verified (Ekomilk Milkana
/// KAM98-2A - see Kam98A2AAnalyserFrameParser), it turns out the reverse of
/// what this comment originally assumed: Clr is reliably populated, but
/// Temperature is not - the KAM98-2A does not measure it at all, so
/// Temperature is nullable for exactly the same "don't fabricate a device
/// reading" reason Clr already was (see CLAUDE.md "Device / serial
/// integration discipline"). Water and Protein (also reported by the
/// KAM98-2A) have no dedicated properties yet - they go in
/// OptionalParameters, the extensibility point this record already had for
/// exactly this situation.
/// </summary>
public sealed record MilkQualityReading
{
    public required decimal Fat { get; init; }
    public required decimal Snf { get; init; }
    public decimal? Clr { get; init; }
    public decimal? Temperature { get; init; }
    public IReadOnlyDictionary<string, decimal>? OptionalParameters { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string DeviceId { get; init; }
    public byte[]? RawData { get; init; }
}
