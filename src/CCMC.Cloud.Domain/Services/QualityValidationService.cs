using CCMC.Cloud.Domain.Enums;

namespace CCMC.Cloud.Domain.Services;

public sealed record QualityReadingInput(decimal Fat, decimal Snf, decimal Temperature);

public sealed record ResolvedQualityRule(decimal MinValue, decimal MaxValue);

public sealed record QualityValidationResult(TransactionStatus Status, string? Reason)
{
    public static QualityValidationResult Accepted() => new(TransactionStatus.Accepted, null);
    public static QualityValidationResult Hold(string reason) => new(TransactionStatus.Hold, reason);
}

/// <summary>
/// The server's authoritative quality validation - the cloud is the final
/// word on ACCEPTED/HOLD, never merely trusting a client-side evaluation
/// (this session's explicit instruction: "Do not automatically reject milk
/// solely based on client-side quality rule evaluation"). Deliberately kept
/// identical in behavior to the Windows client's own QualityValidationService
/// (independently implemented here, not shared code) so both sides reach the
/// same decision for the same inputs: out-of-range readings only ever
/// produce HOLD, never an automatic REJECTED - REJECTED is reachable only
/// through a Manager's explicit override of a HOLD.
/// </summary>
public static class QualityValidationService
{
    public static QualityValidationResult Validate(
        QualityReadingInput reading,
        IReadOnlyDictionary<QualityParameter, ResolvedQualityRule> rules)
    {
        var failures = new List<string>();

        void Check(QualityParameter parameter, decimal value)
        {
            if (!rules.TryGetValue(parameter, out var rule))
            {
                throw new InvalidOperationException(
                    $"No resolved quality rule supplied for parameter {parameter}. " +
                    "Callers must resolve every required rule before calling Validate().");
            }

            if (value < rule.MinValue || value > rule.MaxValue)
            {
                failures.Add($"{parameter} ({value}) outside configured range [{rule.MinValue}, {rule.MaxValue}]");
            }
        }

        Check(QualityParameter.Fat, reading.Fat);
        Check(QualityParameter.Snf, reading.Snf);
        Check(QualityParameter.Temperature, reading.Temperature);

        return failures.Count == 0
            ? QualityValidationResult.Accepted()
            : QualityValidationResult.Hold($"Quality outside limits: {string.Join("; ", failures)}");
    }
}
