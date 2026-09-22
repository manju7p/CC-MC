using CCMC.Domain.Enums;

namespace CCMC.Domain.Services;

/// <summary>Fat/Snf/Weight as currently typed/read at reception - nullable because the UI's live preview calls this before every field is filled in (BRD v5.0 section 25: "If FAT, SNF, or Weight is blank, Rate and Amount are both set to 0").</summary>
public sealed record RateCalculationInput(decimal? Fat, decimal? Snf, decimal? Weight);

/// <summary>Resolved PREFS_RATE_* configuration for the centre (see RateFormulaSettings) - null means "no configuration resolved at all", which BRD v5.0 section 25 also treats as a 0/0 result.</summary>
public sealed record RateFormulaConfig(RateFormulaType RateType, decimal? Value1, decimal? Value2, decimal? TsRate);

public sealed record RateCalculationResult(decimal Rate, decimal Amount)
{
    public static readonly RateCalculationResult Zero = new(0m, 0m);
}

/// <summary>
/// Pure domain calculation, ported exactly from BRD v5.0 section 25 (itself
/// ported from the legacy Android reference implementation's
/// MilkCollectionFragment.getAmount()/RateFormulaFragment). Unlike
/// QualityValidationService, there is deliberately no separate
/// CCMC.Cloud.Domain.Services copy of this class (a previous doc comment
/// here incorrectly claimed one existed - corrected 2026-09-18, no such type
/// is defined anywhere in the solution): the BRD's own wording ("Rate is
/// recalculated live... on the milk collection screen") makes this a
/// client-side, capture-time calculation, and the cloud's ReceptionService
/// only ever persists the Rate/Amount the client already computed and sent
/// (CreateReceptionCommand.Rate/Amount) - it never recomputes them. This is
/// the single authoritative implementation of BRD section 25 in the whole
/// solution.
///
/// Rounding: the BRD gives the Rate formula, then "Amount = Rate x Weight",
/// then separately (section 25.4, "Output Formatting") states both Rate and
/// Amount are rounded to 2 decimal places before being displayed/used. Read
/// literally in that order, rounding is a final formatting step applied
/// independently to the full-precision Rate and the full-precision Amount
/// (Rate x Weight) - not a rounded Rate fed back into the Amount
/// multiplication. This is the literal reading of the BRD's own section
/// ordering, not an invented business rule; MidpointRounding.AwayFromZero
/// (standard "round half up") is used since the BRD does not specify a
/// rounding mode and this is the conventional choice for a monetary/rate
/// value.
/// </summary>
public static class RateCalculationService
{
    private const decimal FatWeightingFactor = 0.22m;
    private const decimal SnfWeightingFactor = 0.36m;
    private const decimal FixedAdjustment = 0.32m;

    public static RateCalculationResult Calculate(RateCalculationInput input, RateFormulaConfig? config)
    {
        if (input.Fat is not { } fat || input.Snf is not { } snf || input.Weight is not { } weight)
        {
            return RateCalculationResult.Zero;
        }

        if (config is null)
        {
            return RateCalculationResult.Zero;
        }

        decimal rate;
        switch (config.RateType)
        {
            case RateFormulaType.FatVsSnf:
                if (config.Value1 is not { } value1 || config.Value2 is not { } value2)
                {
                    return RateCalculationResult.Zero;
                }

                var combinedValue = value1 + value2;
                rate = combinedValue * FatWeightingFactor * (fat / 100m)
                     + combinedValue * SnfWeightingFactor * (snf / 100m)
                     + FixedAdjustment;
                break;

            case RateFormulaType.TsBased:
                if (config.TsRate is not { } tsRate)
                {
                    return RateCalculationResult.Zero;
                }

                var ts = fat + snf;
                rate = (ts * tsRate) / 100m;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(config), $"Unknown rate formula type: {config.RateType}");
        }

        var amount = rate * weight;

        return new RateCalculationResult(
            Math.Round(rate, 2, MidpointRounding.AwayFromZero),
            Math.Round(amount, 2, MidpointRounding.AwayFromZero));
    }
}
