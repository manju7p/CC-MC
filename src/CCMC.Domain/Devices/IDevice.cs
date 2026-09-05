using CCMC.Domain.Enums;

namespace CCMC.Domain.Devices;

/// <summary>
/// Base contract every physical device adapter implements. Deliberately
/// carries no manufacturer-specific members - the reception workflow (and
/// everything in CCMC.Application) must be able to depend only on this and
/// IWeighingScale/IMilkAnalyser, never on a concrete adapter type.
/// </summary>
public interface IDevice
{
    string DeviceId { get; }
    DeviceConnectionState State { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Raised whenever State changes, so the UI can reflect connectivity live.</summary>
    event EventHandler<DeviceConnectionState>? StateChanged;
}
