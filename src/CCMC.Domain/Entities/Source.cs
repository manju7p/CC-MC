using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

public sealed class Source
{
    public required int Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Location { get; init; }
    public string? Contact { get; init; }
    public string? MilkType { get; init; }
    public RecordStatus Status { get; init; } = RecordStatus.Active;
    public required int CentreId { get; init; }
}
