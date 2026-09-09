using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

/// <summary>A Manager resolving a HOLD transaction to ACCEPTED/REJECTED.</summary>
public sealed class TransactionOverride
{
    public long Id { get; set; }
    public required long TransactionLocalId { get; init; }
    public required TransactionStatus OriginalStatus { get; init; }
    public required TransactionStatus NewStatus { get; init; }
    public required int PerformedByUserId { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
