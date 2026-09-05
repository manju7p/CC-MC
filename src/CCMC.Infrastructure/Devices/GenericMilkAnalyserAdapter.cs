using CCMC.Domain.Devices;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Serial;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Adapter boundary for the milk analyser. No manufacturer, model, or serial
/// configuration is verified for this device at all yet (BRD v2 section 5.2
/// lists every parameter as "Configurable", none confirmed) - so, unlike the
/// weighing scale, even the serial settings here are placeholders supplied by
/// whatever DeviceConfiguration an operator/admin enters via the device
/// configuration screen. Same "capture, don't decode" contract as
/// VideoconWeighingScaleAdapter - see its doc comment.
/// </summary>
public sealed class GenericMilkAnalyserAdapter(
    SerialConnectionManager connectionManager,
    SerialConfiguration configuration,
    RawCaptureLogger captureLogger,
    ILogger<GenericMilkAnalyserAdapter> logger)
    : SerialDeviceAdapterBase(connectionManager, configuration, DeviceIdConstant, captureLogger, logger), IMilkAnalyser
{
    public const string DeviceIdConstant = "milk-analyser-generic";

    public async Task<MilkQualityReading> ReadQualityAsync(CancellationToken cancellationToken)
    {
        var raw = await ReadRawAsync(cancellationToken);

        Logger.LogWarning(
            "Milk analyser protocol not established - {ByteCount} byte(s) received but not decoded.", raw.Length);

        throw new DeviceProtocolNotEstablishedException(
            DeviceId,
            raw.Length > 0
                ? $"Received {raw.Length} raw byte(s), captured to the raw capture log, but no verified " +
                  "milk analyser protocol decoder exists yet."
                : "No verified milk analyser protocol decoder exists yet, and no bytes were received in this read window.");
    }
}
