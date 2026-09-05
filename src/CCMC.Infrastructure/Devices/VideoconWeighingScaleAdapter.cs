using CCMC.Domain.Devices;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Serial;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Adapter boundary for the physically verified Videocon Precision Systems
/// weighing scale (COM4 / 2400 / 8-N-1 / no flow control - see
/// SerialConfiguration.VerifiedWeighingScaleDefault and CLAUDE.md "Hardware
/// Verification"). The device's transmission format is now verified (a
/// continuous ASCII stream of "[+-]00000.XXX Kg\r\n" frames, cross-checked
/// live against the scale's own front-panel display - see CLAUDE.md
/// "Hardware Verification" for the full evidence trail) and decoded via
/// VideoconWeightFrameParser. ReadWeightAsync never fabricates a value: if
/// no complete, well-formed frame is present in a given read (e.g. the read
/// window closed mid-frame, or nothing arrived), it throws
/// DeviceParseException rather than guessing - callers must catch this and
/// offer manual entry (BRD v2 section 15), same as before.
/// </summary>
public sealed class VideoconWeighingScaleAdapter(
    SerialConnectionManager connectionManager,
    SerialConfiguration configuration,
    RawCaptureLogger captureLogger,
    ILogger<VideoconWeighingScaleAdapter> logger)
    : SerialDeviceAdapterBase(connectionManager, configuration, DeviceIdConstant, captureLogger, logger), IWeighingScale
{
    public const string DeviceIdConstant = "weighing-scale-videocon";

    public async Task<WeightReading> ReadWeightAsync(CancellationToken cancellationToken)
    {
        var raw = await ReadRawAsync(cancellationToken);

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, DateTimeOffset.UtcNow);
        if (reading is not null)
        {
            // Byte count and success only - the actual frame content already
            // went to the raw capture log via ReadRawAsync above, never
            // duplicated into the general application log.
            Logger.LogInformation(
                "Videocon frame decoded from {ByteCount} byte(s): {Value} {Unit} (stable={Stable}).",
                raw.Length, reading.Value, reading.Unit, reading.Stable);
            return reading;
        }

        Logger.LogWarning(
            "Videocon frame not decodable this read - {ByteCount} byte(s) received but no complete, well-formed frame was present.",
            raw.Length);

        throw new DeviceParseException(
            DeviceId,
            raw.Length > 0
                ? $"Received {raw.Length} raw byte(s), captured to the raw capture log, but no complete " +
                  "'[+-]00000.XXX Kg' frame was present in this read window (the device transmits " +
                  "continuously, so a read can start/end mid-frame - see CLAUDE.md 'Hardware Verification')."
                : "No bytes were received in this read window.",
            raw);
    }
}
