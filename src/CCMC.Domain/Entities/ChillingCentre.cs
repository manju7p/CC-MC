using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

public sealed class ChillingCentre
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public RecordStatus Status { get; init; } = RecordStatus.Active;
}
