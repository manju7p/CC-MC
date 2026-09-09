using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

/// <summary>Null CentreId = global default rule, resolved centre-specific-over-global.</summary>
public sealed class QualityRule
{
    public required int Id { get; init; }
    public required QualityParameter Parameter { get; init; }
    public required decimal MinValue { get; init; }
    public required decimal MaxValue { get; init; }
    public int? CentreId { get; init; }
}
