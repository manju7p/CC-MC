namespace CCMC.Domain.ValueObjects;

/// <summary>
/// Normalized weighing-scale reading (BRD v2 Technical Design section 10,
/// minimum fields: Value, Unit, Stable, Timestamp, DeviceId, RawData).
/// </summary>
public sealed record WeightReading
{
    public required decimal Value { get; init; }
    public required string Unit { get; init; }

    /// <summary>
    /// Whether the scale reported the reading as settled/stable. ASSUMED
    /// field (not BRD-confirmed, not hardware-confirmed) - a plausible flag
    /// real scales commonly report, carried over as a documented assumption
    /// rather than removed, since the parser boundary needs somewhere to put
    /// it once a real protocol is established. Defaults to false (unknown)
    /// until a real parser can actually determine it.
    /// </summary>
    public bool Stable { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
    public required string DeviceId { get; init; }

    /// <summary>Raw bytes the parser derived this reading from, kept for provenance/audit.</summary>
    public byte[]? RawData { get; init; }
}
