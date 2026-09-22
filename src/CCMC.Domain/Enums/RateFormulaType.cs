namespace CCMC.Domain.Enums;

/// <summary>
/// BRD v5.0 section 25 (PREFS_RATE_TYPE): FatVsSnf is the literal
/// "Fat_vs_SNF" mode; TsBased is "any other rate type" per the BRD's own
/// wording (Total-Solids-based, priced against a single flat TS rate).
/// </summary>
public enum RateFormulaType
{
    FatVsSnf,
    TsBased,
}
