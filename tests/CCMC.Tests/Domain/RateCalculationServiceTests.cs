using CCMC.Domain.Enums;
using CCMC.Domain.Services;
using Xunit;

namespace CCMC.Tests.Domain;

/// <summary>BRD v5.0 section 25 - exact formula, ported from the legacy Android reference implementation.</summary>
public class RateCalculationServiceTests
{
    private static readonly RateFormulaConfig FatVsSnfConfig = new(RateFormulaType.FatVsSnf, Value1: 10m, Value2: 8m, TsRate: null);
    private static readonly RateFormulaConfig TsBasedConfig = new(RateFormulaType.TsBased, Value1: null, Value2: null, TsRate: 5m);

    [Fact]
    public void Calculate_FatVsSnf_NormalCase_MatchesBrdFormulaExactly()
    {
        // combined=18; rate = 18*0.22*(4.5/100) + 18*0.36*(9.0/100) + 0.32
        //             = 0.1782 + 0.5832 + 0.32 = 1.0814 -> 1.08
        // amount = 1.0814 * 45.5 = 49.2037 -> 49.20
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), FatVsSnfConfig);

        Assert.Equal(1.08m, result.Rate);
        Assert.Equal(49.20m, result.Amount);
    }

    [Fact]
    public void Calculate_FatVsSnf_DecimalInputs_ComputesCorrectly()
    {
        var config = new RateFormulaConfig(RateFormulaType.FatVsSnf, Value1: 12.5m, Value2: 7.75m, TsRate: null);
        // combined=20.25; rate = 20.25*0.22*(3.9/100) + 20.25*0.36*(8.3/100) + 0.32
        //                = 20.25*0.22=4.455; 4.455*0.039=0.173745
        //                = 20.25*0.36=7.29; 7.29*0.083=0.60507
        //                sum = 0.173745+0.60507+0.32 = 1.098815 -> 1.10
        var result = RateCalculationService.Calculate(new RateCalculationInput(3.9m, 8.3m, 30m), config);

        Assert.Equal(1.10m, result.Rate);
    }

    [Fact]
    public void Calculate_FatVsSnf_RoundsToTwoDecimalPlaces()
    {
        var config = new RateFormulaConfig(RateFormulaType.FatVsSnf, Value1: 1m, Value2: 1m, TsRate: null);
        // combined=2; rate = 2*0.22*0.01 + 2*0.36*0.01 + 0.32 = 0.0044+0.0072+0.32 = 0.3316 -> 0.33
        var result = RateCalculationService.Calculate(new RateCalculationInput(1m, 1m, 1m), config);
        Assert.Equal(0.33m, result.Rate);
    }

    [Fact]
    public void Calculate_TsBased_NormalCase_MatchesBrdFormulaExactly()
    {
        // TS=13.5; rate=(13.5*5)/100=0.675 -> 0.68 (midpoint, away from zero); amount=0.675*45.5=30.7125 -> 30.71
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), TsBasedConfig);

        Assert.Equal(0.68m, result.Rate);
        Assert.Equal(30.71m, result.Amount);
    }

    [Fact]
    public void Calculate_TsBased_Rounding_HalfRoundsAwayFromZero()
    {
        // TS=10; rate=(10*2.5)/100=0.25 - not a midpoint case, sanity check of the division itself.
        var config = new RateFormulaConfig(RateFormulaType.TsBased, null, null, TsRate: 2.5m);
        var result = RateCalculationService.Calculate(new RateCalculationInput(4m, 6m, 10m), config);

        Assert.Equal(0.25m, result.Rate);
        Assert.Equal(2.50m, result.Amount);
    }

    [Fact]
    public void Calculate_BlankFat_ReturnsZero()
    {
        var result = RateCalculationService.Calculate(new RateCalculationInput(null, 9.0m, 45.5m), FatVsSnfConfig);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_BlankSnf_ReturnsZero()
    {
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, null, 45.5m), FatVsSnfConfig);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_BlankWeight_ReturnsZero()
    {
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, null), FatVsSnfConfig);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_NoConfigAtAll_ReturnsZero()
    {
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), config: null);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_FatVsSnf_MissingValue1_ReturnsZero()
    {
        var config = new RateFormulaConfig(RateFormulaType.FatVsSnf, Value1: null, Value2: 8m, TsRate: null);
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), config);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_FatVsSnf_MissingValue2_ReturnsZero()
    {
        var config = new RateFormulaConfig(RateFormulaType.FatVsSnf, Value1: 10m, Value2: null, TsRate: null);
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), config);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_TsBased_MissingTsRate_ReturnsZero()
    {
        var config = new RateFormulaConfig(RateFormulaType.TsBased, Value1: null, Value2: null, TsRate: null);
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), config);
        Assert.Equal(RateCalculationResult.Zero, result);
    }

    [Fact]
    public void Calculate_ZeroFatAndSnf_FatVsSnf_ReturnsFixedAdjustmentOnly()
    {
        // combined doesn't matter when fat=snf=0: rate = 0 + 0 + 0.32 = 0.32
        var result = RateCalculationService.Calculate(new RateCalculationInput(0m, 0m, 10m), FatVsSnfConfig);
        Assert.Equal(0.32m, result.Rate);
        Assert.Equal(3.20m, result.Amount);
    }

    [Fact]
    public void Calculate_ZeroWeight_FatVsSnf_RateNonZero_AmountZero()
    {
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 0m), FatVsSnfConfig);
        Assert.Equal(1.08m, result.Rate);
        Assert.Equal(0m, result.Amount);
    }

    [Fact]
    public void Calculate_CentreSpecificOverGlobal_IsCallerResponsibility_ServiceJustUsesWhatItIsGiven()
    {
        // RateCalculationService itself is a pure function - centre-specific-over-global
        // resolution happens one layer up (IRateFormulaSettingsRepository.ResolveForCentreAsync).
        // This test documents that boundary: passing the "resolved" config directly still
        // produces the exact same result as any other config.
        var globalConfig = new RateFormulaConfig(RateFormulaType.TsBased, null, null, TsRate: 5m);
        var result = RateCalculationService.Calculate(new RateCalculationInput(4.5m, 9.0m, 45.5m), globalConfig);
        Assert.Equal(0.68m, result.Rate);
    }

    /// <summary>
    /// Discriminating case (BRD verification task, Phase 3): a deliberately
    /// chosen input where "Amount from the full-precision Rate" and "Amount
    /// from the 2-decimal-rounded Rate" produce genuinely DIFFERENT 2-decimal
    /// results - proving which code path actually runs, not just that both
    /// approaches happen to agree.
    ///
    /// TS-based mode, FAT=3.00 SNF=4.00 (TS=7.00), TsRate=15.05, Weight=45.5:
    ///   raw Rate     = (7.00 * 15.05) / 100 = 105.35 / 100 = 1.0535
    ///   rounded Rate = 1.05 (remainder 0.0035 &lt; 0.005, rounds down)
    ///   Amount if RAW rate used:     1.0535 * 45.5 = 47.93425 -&gt; rounds to 47.93
    ///   Amount if ROUNDED rate used: 1.05   * 45.5 = 47.775   -&gt; rounds to 47.78 (exact midpoint, AwayFromZero)
    /// These differ (47.93 vs 47.78) - a real, provable discrimination, not a coincidence.
    ///
    /// BRD v5.0 section 25.2/25.3 defines "Rate = &lt;formula&gt;" then "Amount = Rate x Weight"
    /// as two lines with NO rounding mentioned yet; section 25.4 ("Output Formatting")
    /// is introduced only afterward, as a separate step "before being displayed and used
    /// for printing/SMS receipts" - i.e. rounding is output formatting applied to both
    /// already-computed full-precision quantities, not an intermediate step that feeds
    /// back into the Amount formula. Read literally, in that order, BRD requires the
    /// RAW-rate result (47.93) - which is what RateCalculationService actually returns,
    /// confirmed here.
    /// </summary>
    [Fact]
    public void Calculate_TsBased_DiscriminatingCase_UsesFullPrecisionRateForAmount_NotRoundedRate()
    {
        var config = new RateFormulaConfig(RateFormulaType.TsBased, Value1: null, Value2: null, TsRate: 15.05m);

        var result = RateCalculationService.Calculate(new RateCalculationInput(3.00m, 4.00m, 45.5m), config);

        const decimal amountIfRawRateUsed = 47.93m;
        const decimal amountIfRoundedRateUsed = 47.78m;

        Assert.Equal(1.05m, result.Rate); // the displayed/stored Rate is still rounded
        Assert.Equal(amountIfRawRateUsed, result.Amount); // but Amount was derived from the UNROUNDED 1.0535, not 1.05
        Assert.NotEqual(amountIfRoundedRateUsed, result.Amount); // explicitly rule out the other interpretation
    }
}
