namespace CCMC.Infrastructure.Logging;

/// <summary>
/// Appends raw bytes received from a device to a per-device, per-day capture
/// log (timestamp + length + hex dump, one line per read). This is the
/// "raw capture infrastructure" required so a real protocol can eventually be
/// reverse-engineered from controlled captures (BRD v2 section 8 / this
/// session's "Critical Device Integration Rule") - it is NOT itself a parser
/// and makes no attempt to interpret the bytes.
/// </summary>
public sealed class RawCaptureLogger(string capturesDirectory)
{
    public void Append(string deviceId, byte[] data)
    {
        if (data.Length == 0) return;

        Directory.CreateDirectory(capturesDirectory);
        var path = Path.Combine(capturesDirectory, $"{SanitizeFileName(deviceId)}_{DateTimeOffset.UtcNow:yyyyMMdd}.capture.log");
        var line = $"{DateTimeOffset.UtcNow:O}\t{data.Length}\t{Convert.ToHexString(data)}{Environment.NewLine}";
        File.AppendAllText(path, line);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
