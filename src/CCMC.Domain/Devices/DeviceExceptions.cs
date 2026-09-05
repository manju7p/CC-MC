namespace CCMC.Domain.Devices;

/// <summary>
/// A read was attempted while the device was not connected. The caller
/// should offer manual entry / a reconnect action - never crash.
/// </summary>
public sealed class DeviceNotConnectedException(string deviceId)
    : Exception($"Device '{deviceId}' is not connected.")
{
    public string DeviceId { get; } = deviceId;
}

/// <summary>
/// A device is connected, but the concrete adapter's byte-level protocol
/// decoder is not implemented yet, because no verified capture or
/// manufacturer documentation exists for it (Engineering Rule: no invented
/// device protocols). This is the explicit "unavailable, not faked" state
/// required by this session's instructions - callers must catch it and
/// route the operator to manual entry, not treat it as a crash.
/// </summary>
public sealed class DeviceProtocolNotEstablishedException(string deviceId, string reason)
    : Exception($"Device '{deviceId}' has no established protocol decoder: {reason}")
{
    public string DeviceId { get; } = deviceId;
}

/// <summary>Raised when malformed/unparseable bytes are received from a device with a real parser.</summary>
public sealed class DeviceParseException(string deviceId, string reason, byte[]? rawData = null)
    : Exception($"Device '{deviceId}' produced unparseable data: {reason}")
{
    public string DeviceId { get; } = deviceId;
    public byte[]? RawData { get; } = rawData;
}
