using CCMC.Domain.Devices;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Serial;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Shared connection-lifecycle plumbing for a serial-attached device adapter:
/// acquires the single owning SerialPortConnection for its configured COM
/// port via SerialConnectionManager, tracks DeviceConnectionState, and routes
/// every raw read through RawCaptureLogger. Concrete adapters (Videocon
/// scale, milk analyser) add only their own ReadXAsync method and whatever
/// protocol decoding they can actually justify - no manufacturer-specific
/// logic belongs here.
/// </summary>
public abstract class SerialDeviceAdapterBase(
    SerialConnectionManager connectionManager,
    SerialConfiguration configuration,
    string deviceId,
    RawCaptureLogger captureLogger,
    ILogger logger) : IDevice
{
    private SerialPortConnection? _connection;

    public string DeviceId { get; } = deviceId;
    public DeviceConnectionState State { get; private set; } = DeviceConnectionState.Disconnected;
    public event EventHandler<DeviceConnectionState>? StateChanged;

    protected SerialConfiguration Configuration { get; } = configuration;

    /// <summary>Exposed so concrete adapters (e.g. for a Parser-category "not decoded" log) can log without each needing its own captured field for the same instance.</summary>
    protected ILogger Logger { get; } = logger;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (State == DeviceConnectionState.Connected) return;

        Logger.LogInformation("Connecting {DeviceId} on {ComPort} ({BaudRate} baud)...", DeviceId, Configuration.ComPort, Configuration.BaudRate);
        SetState(DeviceConnectionState.Connecting);
        try
        {
            _connection = connectionManager.Acquire(Configuration);
            await _connection.OpenAsync(cancellationToken);
            SetState(DeviceConnectionState.Connected);
            Logger.LogInformation("Connected {DeviceId} on {ComPort}.", DeviceId, Configuration.ComPort);
            Logger.LogInformation("Runtime serial settings for {DeviceId}: {Settings}", DeviceId, _connection.DescribeRuntimeSettings());
        }
        catch (Exception ex)
        {
            if (_connection is not null)
            {
                connectionManager.Release(Configuration.ComPort);
                _connection = null;
            }
            SetState(DeviceConnectionState.Error);
            Logger.LogWarning(ex, "Failed to connect {DeviceId} on {ComPort}.", DeviceId, Configuration.ComPort);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync(cancellationToken);
            connectionManager.Release(Configuration.ComPort);
            _connection = null;
        }
        SetState(DeviceConnectionState.Disconnected);
        Logger.LogInformation("Disconnected {DeviceId}.", DeviceId);
    }

    /// <summary>
    /// Reads whatever bytes arrive within this device's configured timeout,
    /// capturing them for later protocol analysis. Logs only the byte count,
    /// never the raw content - the actual bytes go exclusively to
    /// RawCaptureLogger's dedicated per-device capture file, keeping the
    /// general application log readable rather than a raw byte dump.
    /// </summary>
    protected async Task<byte[]> ReadRawAsync(CancellationToken cancellationToken)
    {
        if (_connection is null || State != DeviceConnectionState.Connected)
        {
            Logger.LogWarning("Read attempted on {DeviceId} while not connected.", DeviceId);
            throw new DeviceNotConnectedException(DeviceId);
        }

        var data = await _connection.ReadAvailableAsync(TimeSpan.FromMilliseconds(Configuration.ReadTimeoutMs), cancellationToken);
        captureLogger.Append(DeviceId, data);

        if (data.Length == 0)
        {
            Logger.LogDebug("Read timeout on {DeviceId} - no bytes received within {TimeoutMs}ms.", DeviceId, Configuration.ReadTimeoutMs);
        }
        else
        {
            Logger.LogDebug("Received {ByteCount} byte(s) from {DeviceId} (see raw capture log for content).", data.Length, DeviceId);
        }

        return data;
    }

    private void SetState(DeviceConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
