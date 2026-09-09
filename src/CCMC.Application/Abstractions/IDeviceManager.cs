using CCMC.Domain.Devices;
using CCMC.Domain.Enums;

namespace CCMC.Application.Abstractions;

/// <summary>
/// Owns device lifecycle for the whole app - loads configuration, opens/
/// closes the (single) owner connection per COM port, exposes current
/// devices to the reception workflow. Concrete implementation lives in
/// CCMC.Infrastructure (it needs SerialConnectionManager + the concrete
/// adapters); CCMC.Application only depends on this abstraction plus
/// IWeighingScale/IMilkAnalyser.
/// </summary>
public interface IDeviceManager
{
    IWeighingScale? WeighingScale { get; }
    IMilkAnalyser? MilkAnalyser { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
    Task<DeviceConnectionState> TestConnectionAsync(DeviceKind kind, CancellationToken cancellationToken);
}
