using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

/// <summary>
/// BRD v5.0 section 25's PREFS_RATE_* configuration, ported from the legacy
/// RateFormulaFragment settings screen. Null CentreId = global default,
/// resolved centre-specific-over-global (same convention as QualityRule -
/// see its doc comment). Value1/Value2/TsRate are nullable because the BRD
/// explicitly allows an unconfigured formula: Rate/Amount must then be 0,
/// never a fabricated default (see RateCalculationService).
/// </summary>
public sealed class RateFormulaSettings
{
    public required int Id { get; init; }
    public required RateFormulaType RateType { get; init; }
    public decimal? Value1 { get; init; }
    public decimal? Value2 { get; init; }
    public decimal? TsRate { get; init; }
    public int? CentreId { get; init; }
}
