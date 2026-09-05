using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CCMC.Domain.ValueObjects;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Parses the Videocon weighing scale's now-verified live transmission format:
/// a continuous ASCII stream of CRLF-terminated frames, each exactly
/// "[+-]00000.XXX Kg" (13 characters, 3-decimal kg resolution). Verified via
/// live captures on the physical device (COM4/2400/8-N-1/no flow control),
/// cross-checked against the scale's own front-panel display, and confirmed
/// deterministic across 1000+ captured frames with zero malformed exceptions
/// - see CLAUDE.md "Hardware Verification" for the full evidence trail.
///
/// A single serial read does NOT reliably align with frame boundaries - the
/// device transmits continuously, so a bounded read can (and, per real
/// captures, routinely does) start or end mid-frame. Only a frame with a
/// CRLF on BOTH sides within the same read is trusted; the first and last
/// CRLF-delimited segments are always treated as potentially-incomplete
/// fragments and discarded, even if they happen to look well-formed.
/// </summary>
public static class VideoconWeightFrameParser
{
    private const string Unit = "kg";
    private static readonly Regex FramePattern = new(@"^[+-]\d{5}\.\d{3} Kg$", RegexOptions.Compiled);

    /// <summary>
    /// Returns the most recent complete, valid frame found in <paramref name="raw"/>,
    /// or null if no complete valid frame is present in this read (never
    /// fabricates a reading - the caller must treat null as "unavailable this
    /// cycle", not an error).
    /// </summary>
    public static WeightReading? ParseLatest(byte[] raw, string deviceId, DateTimeOffset timestamp)
    {
        if (raw.Length == 0) return null;

        var text = Encoding.ASCII.GetString(raw);
        var segments = text.Split("\r\n");

        // Only segments strictly between two CRLFs are trusted as complete -
        // the first and last segments may be fragments cut by this read's
        // buffer boundary (proven by real captures: e.g. a trailing "+000"
        // with the rest of the frame arriving only in the next read).
        if (segments.Length < 3) return null;
        var completeFrames = segments[1..^1];

        var validFrames = completeFrames.Where(f => FramePattern.IsMatch(f)).ToList();
        if (validFrames.Count == 0) return null;

        var latest = validFrames[^1];
        var numericPart = latest[..^3]; // strip the trailing " Kg"
        if (!decimal.TryParse(numericPart, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // Evidence-based, NOT device-reported: no separate stability flag
        // exists in this format. If the last two valid frames in this read
        // are byte-identical, the reading held steady across at least two
        // consecutive transmissions - a reasonable, documented derivation,
        // not a fabricated device capability.
        var stable = validFrames.Count >= 2 && validFrames[^1] == validFrames[^2];

        return new WeightReading
        {
            Value = value,
            Unit = Unit,
            Stable = stable,
            Timestamp = timestamp,
            DeviceId = deviceId,
            RawData = raw,
        };
    }
}
