using CCMC.Domain.Enums;
using CCMC.Domain.Services;
using Xunit;

namespace CCMC.Tests.Domain;

public class QualityValidationServiceTests
{
    private static readonly IReadOnlyDictionary<QualityParameter, ResolvedQualityRule> DefaultRules =
        new Dictionary<QualityParameter, ResolvedQualityRule>
        {
            [QualityParameter.Fat] = new ResolvedQualityRule(3.0m, 6.0m),
            [QualityParameter.Snf] = new ResolvedQualityRule(8.0m, 10.0m),
            [QualityParameter.Temperature] = new ResolvedQualityRule(0m, 10.0m),
        };

    [Fact]
    public void Validate_AllWithinRange_ReturnsAccepted()
    {
        var result = QualityValidationService.Validate(new QualityReadingInput(4.5m, 9.0m, 5.0m), DefaultRules);

        Assert.Equal(TransactionStatus.Accepted, result.Status);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(2.9, 9.0, 5.0)] // FAT below min
    [InlineData(6.1, 9.0, 5.0)] // FAT above max
    [InlineData(4.5, 7.9, 5.0)] // SNF below min
    [InlineData(4.5, 9.0, 10.1)] // Temperature above max
    public void Validate_AnyParameterOutOfRange_ReturnsHold_NeverRejected(decimal fat, decimal snf, decimal temperature)
    {
        var result = QualityValidationService.Validate(new QualityReadingInput(fat, snf, temperature), DefaultRules);

        // Engineering rule (documented from the cloud API's own behaviour, context.md):
        // the system never auto-rejects - only ever ACCEPTED or HOLD.
        Assert.Equal(TransactionStatus.Hold, result.Status);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Validate_MultipleFailures_ListsEveryFailureInReason()
    {
        var result = QualityValidationService.Validate(new QualityReadingInput(1.0m, 1.0m, 99.0m), DefaultRules);

        Assert.Equal(TransactionStatus.Hold, result.Status);
        Assert.Contains("Fat", result.Reason);
        Assert.Contains("Snf", result.Reason);
        Assert.Contains("Temperature", result.Reason);
    }

    [Fact]
    public void Validate_MissingRuleForRequiredParameter_Throws()
    {
        var incompleteRules = new Dictionary<QualityParameter, ResolvedQualityRule>
        {
            [QualityParameter.Fat] = new ResolvedQualityRule(3.0m, 6.0m),
            // SNF and Temperature deliberately omitted
        };

        Assert.Throws<InvalidOperationException>(() =>
            QualityValidationService.Validate(new QualityReadingInput(4.5m, 9.0m, 5.0m), incompleteRules));
    }

    [Theory]
    [InlineData(3.0, 8.0, 0)] // exactly at each minimum boundary
    [InlineData(6.0, 10.0, 10)] // exactly at each maximum boundary
    public void Validate_BoundaryValuesInclusive_ReturnsAccepted(decimal fat, decimal snf, decimal temperature)
    {
        var result = QualityValidationService.Validate(new QualityReadingInput(fat, snf, temperature), DefaultRules);
        Assert.Equal(TransactionStatus.Accepted, result.Status);
    }
}
