using CCMC.Domain.Devices;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Serial;
using Microsoft.Extensions.Logging;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Adapter boundary for the physical Ekomilk Milkana KAM98-2A milk analyser.
/// The device's OUTPUT PAYLOAD format is now derived from two real observed
/// captures (see Kam98A2AAnalyserFrameParser's doc comment for the full field
/// mapping and both source samples) - but unlike VideoconWeighingScaleAdapter,
/// the physical serial connection itself (COM port, baud rate, framing over
/// real RS232 traffic) has NOT been exercised against the actual hardware in
/// this pass, because the device was not available (see CLAUDE.md "Hardware
/// Verification"). This adapter is therefore ready to be tested against the
/// real device once available, but that hardware-in-the-loop step is still
/// outstanding - do not treat this class's existence as proof the serial
/// link itself works.
///
/// Same "never fabricate a reading" contract as every other adapter: if no
/// complete, well-formed "(29 digits)" frame is present in a given read, this
/// throws DeviceParseException rather than guessing - callers must catch this
/// and offer manual entry (BRD v2 section 15), same as
/// VideoconWeighingScaleAdapter.ReadWeightAsync.
/// </summary>
public sealed class EkomilkKam98A2AAnalyserAdapter(
    SerialConnectionManager connectionManager,
    SerialConfiguration configuration,
    RawCaptureLogger captureLogger,
    ILogger<EkomilkKam98A2AAnalyserAdapter> logger)
    : SerialDeviceAdapterBase(connectionManager, configuration, DeviceIdConstant, captureLogger, logger), IMilkAnalyser
{
    public const string DeviceIdConstant = "milk-analyser-ekomilk-kam98-2a";

    public async Task<MilkQualityReading> ReadQualityAsync(CancellationToken cancellationToken)
    {
        var raw = await ReadRawAsync(cancellationToken);

        var reading = Kam98A2AAnalyserFrameParser.ParseLatest(raw, DeviceId, DateTimeOffset.UtcNow);
        if (reading is not null)
        {
            Logger.LogInformation(
                "KAM98-2A frame decoded from {ByteCount} byte(s): Fat={Fat} Snf={Snf} Clr={Clr}.",
                raw.Length, reading.Fat, reading.Snf, reading.Clr);
            return reading;
        }

        Logger.LogWarning(
            "KAM98-2A frame not decodable this read - {ByteCount} byte(s) received but no complete '(29-digit)' frame was present.",
            raw.Length);

        throw new DeviceParseException(
            DeviceId,
            raw.Length > 0
                ? $"Received {raw.Length} raw byte(s), captured to the raw capture log, but no complete " +
                  "'(29-digit)' frame was present in this read window (the device may transmit continuously, " +
                  "so a read can start/end mid-frame)."
                : "No bytes were received in this read window.",
            raw);
    }
}
