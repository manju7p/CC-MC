using CCMC.Domain.Enums;

namespace CCMC.Domain.Entities;

public sealed class Vehicle
{
    public required int Id { get; init; }
    public required string VehicleNumber { get; init; }
    public string? TankerNumber { get; init; }
    public string? DriverName { get; init; }
    public string? DriverMobile { get; init; }
    public decimal? CapacityKg { get; init; }
    public RecordStatus Status { get; init; } = RecordStatus.Active;
    public required int CentreId { get; init; }
}
