using CCMC.Domain.Enums;

namespace CCMC.Domain.Services;

public sealed record QualityReadingInput(decimal Fat, decimal Snf, decimal Temperature);

public sealed record ResolvedQualityRule(decimal MinValue, decimal MaxValue);

public sealed record QualityValidationResult(TransactionStatus Status, string? Reason)
{
    public static QualityValidationResult Accepted() => new(TransactionStatus.Accepted, null);
    public static QualityValidationResult Hold(string reason) => new(TransactionStatus.Hold, reason);
}

/// <summary>
/// Pure domain validation logic, deliberately mirroring the cloud API's own
/// QualityValidationService exactly (documented in context.md "Cloud
/// Responsibilities" / docs/assumptions.md #auto-reject-vs-hold): out-of-range
/// readings go to HOLD, never straight to REJECTED. REJECTED is reachable only
/// through a later manager override of a HOLD. Keeping this identical to the
/// cloud's own rule matters because a reception validated locally while
/// offline must reach the same ACCEPTED/HOLD decision the cloud would have
/// made online, or an operator's offline and online experience would silently
/// diverge.
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
