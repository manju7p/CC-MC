using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CCMC.Domain.Devices;
using CCMC.Domain.ValueObjects;

namespace CCMC.Infrastructure.Devices;

/// <summary>
/// Parses the Ekomilk Milkana KAM98-2A milk analyser's compact output
/// format: a single parenthesized, 29-digit fixed-width frame, e.g.
/// "(03900830283801210000032404503)". Field boundaries below are derived
/// (not guessed) from two real, independently observed device outputs
/// supplied directly by the project owner - not manufacturer documentation,
/// not a live serial capture (see CLAUDE.md "Hardware Verification" -
/// the physical serial connection itself is NOT yet verified, only this
/// payload decode):
///
///   (03900830283801210000032404503) -&gt; Fat 3.9  Snf 8.3  Clr 28.4  Water 1.21  Protein 3.24
///   (02500520171638900000020906585) -&gt; Fat 2.5  Snf 5.2  Clr 17.2  Water 38.9  Protein 2.09
///
/// Both samples are exactly 29 digits, and every field below reproduces the
/// observed value exactly for both:
///
///   [ 0.. 3)  Fat      3 digits / 10   (1 decimal place)
///   [ 3.. 7)  Snf      4 digits / 10   (1 decimal place)
///   [ 7..12)  Clr      5 digits / 100  (2 decimal places internally - e.g.
///                                       "02838" -&gt; 28.38 - but the
///                                       analyser's own reported figure is
///                                       that value rounded to 1 decimal:
///                                       28.38-&gt;28.4, 17.16-&gt;17.2, matching
///                                       both observed samples exactly)
///   [12..16)  Water    4 digits / 100  (2 decimal places)
///   [16..20)  (unidentified - "0000" in both samples; preserved in RawData, not decoded)
///   [20..24)  Protein  4 digits / 100  (2 decimal places)
///   [24..29)  (unidentified trailer - differs between samples ("04503" vs
///              "06585"); possibly a checksum or an additional parameter -
///              no confident derivation exists from two samples, so per
///              CLAUDE.md "no invented protocol / document known gaps" this
///              is preserved in RawData but not decoded)
///
/// Never fabricates a reading: a payload that isn't exactly 29 digits (after
/// stripping an optional wrapping "(...)" and surrounding whitespace/CR/LF)
/// is rejected, not guessed at.
/// </summary>
public static class Kam98A2AAnalyserFrameParser
{
    private const int PayloadLength = 29;
    private static readonly Regex FramePattern = new(@"\((\d{29})\)", RegexOptions.Compiled);

    /// <summary>
    /// Device path: a serial read may contain zero, one, or several
    /// concatenated "(29 digits)" frames (the device transmits continuously,
    /// same as the Videocon scale - see VideoconWeightFrameParser). Only a
    /// complete, well-formed frame is trusted; the latest one found is
    /// returned. Returns null (never throws) when no complete frame is
    /// present in this read - the caller (the adapter) decides what that
    /// means, same convention as VideoconWeightFrameParser.ParseLatest.
    /// </summary>
    public static MilkQualityReading? ParseLatest(byte[] raw, string deviceId, DateTimeOffset timestamp)
    {
        if (raw.Length == 0) return null;

        var text = Encoding.ASCII.GetString(raw);
        var matches = FramePattern.Matches(text);
        if (matches.Count == 0) return null;

        var digits = matches[^1].Groups[1].Value;
        return Decode(digits, deviceId, timestamp, raw);
    }

    /// <summary>
    /// Manual/test-input path (Reception window's "Milk Analyser - Manual
    /// Test Input" panel): a single string typed or pasted by an operator,
    /// standing in for the physical device while it is unavailable. Feeds
    /// the EXACT SAME field decoding as ParseLatest above - only the framing
    /// step differs (no byte stream to scan, just one payload to validate).
    /// Tolerates an optional single wrapping "(...)" and surrounding
    /// whitespace; throws DeviceParseException (not a silent null) for
    /// anything else, so the UI can show the operator exactly why their
    /// input didn't decode.
    /// </summary>
    public static MilkQualityReading ParsePayload(string payload, string deviceId, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var candidate = payload.Trim();
        if (candidate.Length >= 2 && candidate[0] == '(' && candidate[^1] == ')')
        {
            candidate = candidate[1..^1].Trim();
        }

        var rawBytes = Encoding.ASCII.GetBytes(payload);

        if (candidate.Length != PayloadLength)
        {
            throw new DeviceParseException(
                deviceId,
                $"Expected exactly {PayloadLength} digits (optionally wrapped in parentheses), " +
                $"got {candidate.Length} character(s) in '{payload}'.",
                rawBytes);
        }

        if (!IsAllDigits(candidate))
        {
            throw new DeviceParseException(
                deviceId,
                $"Payload must contain only numeric digits; got '{candidate}'.",
                rawBytes);
        }

        return Decode(candidate, deviceId, timestamp, rawBytes)
            ?? throw new DeviceParseException(deviceId, $"Could not decode payload '{payload}'.", rawBytes);
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
    }

    private static MilkQualityReading? Decode(string digits, string deviceId, DateTimeOffset timestamp, byte[] raw)
    {
        if (digits.Length != PayloadLength || !IsAllDigits(digits)) return null;

        var fat = ExtractField(digits, 0, 3, 10);
        var snf = ExtractField(digits, 3, 4, 10);
        var clrRaw = ExtractField(digits, 7, 5, 100);
        var clr = Math.Round(clrRaw, 1, MidpointRounding.AwayFromZero);
        var water = ExtractField(digits, 12, 4, 100);
        var protein = ExtractField(digits, 20, 4, 100);

        return new MilkQualityReading
        {
            Fat = fat,
            Snf = snf,
            Clr = clr,
            Temperature = null, // the KAM98-2A does not measure temperature - never fabricated, see this record's doc comment
            OptionalParameters = new Dictionary<string, decimal>
            {
                ["Water"] = water,
                ["Protein"] = protein,
            },
            Timestamp = timestamp,
            DeviceId = deviceId,
            RawData = raw,
        };
    }

    private static decimal ExtractField(string digits, int start, int length, int divisor) =>
        int.Parse(digits.AsSpan(start, length), CultureInfo.InvariantCulture) / (decimal)divisor;
}
