using CCMC.Application.Abstractions;
using CCMC.Domain.Devices;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Serial;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Loads device configuration, constructs the concrete adapters, and exposes
/// them to CCMC.Application. Falls back to the physically verified weighing
/// scale default (COM4/2400/8-N-1) when no DeviceConfiguration row exists yet
/// - the app must be usable (with device testing) before an operator has
/// visited the device configuration screen.
///
/// InitializeAsync only reads local SQLite configuration and constructs adapter
/// objects (it does not open any COM port itself - see SerialDeviceAdapterBase.
/// ConnectAsync is what actually calls SerialConnectionManager.Acquire()). Each
/// device's setup is isolated in its own try/catch so a problem loading one
/// device's configuration can never prevent the other from initializing, and
/// can never crash application startup (BRD v2 section 20/29 - "none of these
/// conditions should crash the application").
/// </summary>
public sealed class DeviceManager(
    SerialConnectionManager connectionManager,
    IDeviceConfigurationRepository configurationRepository,
    RawCaptureLogger captureLogger,
    ILoggerFactory loggerFactory) : IDeviceManager
{
    private readonly ILogger<DeviceManager> logger = loggerFactory.CreateLogger<DeviceManager>();

    public IWeighingScale? WeighingScale { get; private set; }
    public IMilkAnalyser? MilkAnalyser { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scaleConfig = await configurationRepository.GetAsync(DeviceKind.WeighingScale, cancellationToken);
            var scaleSerial = scaleConfig?.Serial ?? SerialConfiguration.VerifiedWeighingScaleDefault;
            WeighingScale = new VideoconWeighingScaleAdapter(
                connectionManager, scaleSerial, captureLogger, loggerFactory.CreateLogger<VideoconWeighingScaleAdapter>());
            logger.LogInformation(
                "Weighing scale adapter configured: port={ComPort} baud={BaudRate} (source={ConfigSource})",
                scaleSerial.ComPort, scaleSerial.BaudRate, scaleConfig is null ? "verified default" : "DeviceConfiguration");
        }
        catch (Exception ex)
        {
            // Loading/constructing the scale adapter must never take the whole
            // app down, and must not prevent the analyser from initializing below.
            logger.LogError(ex, "Failed to initialize the weighing scale adapter - it will remain unavailable.");
            WeighingScale = null;
        }

        try
        {
            var analyserConfig = await configurationRepository.GetAsync(DeviceKind.MilkAnalyser, cancellationToken);
            if (analyserConfig is not null)
            {
                MilkAnalyser = new GenericMilkAnalyserAdapter(
                    connectionManager, analyserConfig.Serial, captureLogger, loggerFactory.CreateLogger<GenericMilkAnalyserAdapter>());
                logger.LogInformation(
                    "Milk analyser adapter configured: port={ComPort} baud={BaudRate}",
                    analyserConfig.Serial.ComPort, analyserConfig.Serial.BaudRate);
            }
            else
            {
                // No configuration entered yet - reception workflow treats a null
                // MilkAnalyser as manual-entry-required, not an error.
                MilkAnalyser = null;
                logger.LogInformation("No milk analyser configuration found - analyser reads will require manual entry.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize the milk analyser adapter - it will remain unavailable.");
            MilkAnalyser = null;
        }
    }

    public async Task<DeviceConnectionState> TestConnectionAsync(DeviceKind kind, CancellationToken cancellationToken)
    {
        IDevice? device = kind switch
        {
            DeviceKind.WeighingScale => WeighingScale,
            DeviceKind.MilkAnalyser => MilkAnalyser,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (device is null) return DeviceConnectionState.Disconnected;

        try
        {
            await device.ConnectAsync(cancellationToken);
            return device.State;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test connection failed for {DeviceKind} ({DeviceId})", kind, device.DeviceId);
            return DeviceConnectionState.Error;
        }
    }
}
