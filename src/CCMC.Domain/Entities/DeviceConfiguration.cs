using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;

namespace CCMC.Domain.Entities;

public sealed class DeviceConfiguration
{
    public long Id { get; init; }
    public required DeviceKind Kind { get; init; }
    public required string Name { get; init; }
    public required SerialConfiguration Serial { get; set; }
    public bool IsEnabled { get; set; } = true;
}
