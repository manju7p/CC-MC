using CCMC.Infrastructure.Devices;
using Xunit;

namespace CCMC.Tests.Devices;

/// <summary>
/// Every "valid frame" fixture below is the EXACT byte sequence captured
/// live from the physical Videocon scale (COM4/2400/8-N-1/no flow control),
/// pulled directly from a real %LocalAppData%\CCMC\captures\weighing-scale-videocon_*.capture.log
/// entry during the live-hardware investigation - not invented. The
/// physical scale's own front-panel display was cross-checked against the
/// decoded stream during that investigation (see CLAUDE.md "Hardware
/// Verification"). Malformed/incomplete fixtures are synthetic by
/// necessity (a real device never sends garbage), used only to prove
/// rejection logic.
/// </summary>
public class VideoconWeightFrameParserTests
{
    private const string DeviceId = "weighing-scale-videocon";
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseLatest_RealZeroFrameCapture_DecodesExactZero()
    {
        // Real capture: a leading garbage remnant "s" (a genuine fragment
        // from a previous, unrelated partial frame), two complete
        // "+00000.000 Kg" frames, then an incomplete trailing fragment
        // "+00000.000" (missing " Kg") - captured 2026-09-05T10:33:49 UTC.
        var raw = "s+00000.000 Kg\r\n+00000.000 Kg\r\n+00000.000"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(0.000m, reading!.Value);
        Assert.Equal("kg", reading.Unit);
        Assert.Equal(DeviceId, reading.DeviceId);
    }

    [Fact]
    public void ParseLatest_RealMultiFrameCapture_DecodesLatestValue_MarksStable()
    {
        // Real capture excerpt: 8 identical "+00000.818 Kg" frames back to
        // back - captured 2026-09-05T11:12:18 UTC.
        var raw = "+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n+00000.818 Kg\r\n"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(0.818m, reading!.Value);
        Assert.True(reading.Stable); // last two frames in this read are identical
    }

    [Fact]
    public void ParseLatest_RealTailFragment_IgnoresLeadingAndTrailingIncompleteSegments()
    {
        // Real capture tail: a leading fragment "00.824 Kg" (missing the
        // "+00000." prefix - cut by the previous read's boundary), three
        // complete "+00000.824 Kg" frames, then a trailing incomplete
        // fragment "+000" (the rest of the frame hadn't arrived yet when
        // this read window closed) - captured 2026-09-05T11:12:18 UTC, the
        // exact tail of the same entry as the multi-frame test above.
        var raw = "00.824 Kg\r\n+00000.824 Kg\r\n+00000.824 Kg\r\n+00000.824 Kg\r\n+000"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(0.824m, reading!.Value);
        Assert.True(reading.Stable);
    }

    [Fact]
    public void ParseLatest_NegativeValue_DecodesCorrectly()
    {
        // Real format observed during drift capture (sign flips to '-' when
        // the reading goes below the scale's current zero reference).
        var raw = "-00001.254 Kg\r\n-00001.254 Kg\r\n"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(-1.254m, reading!.Value);
    }

    [Fact]
    public void ParseLatest_EmptyRead_ReturnsNull_NeverThrows()
    {
        var reading = VideoconWeightFrameParser.ParseLatest([], DeviceId, Now);

        Assert.Null(reading);
    }

    [Fact]
    public void ParseLatest_OnlyAFragment_NoCompleteFrame_ReturnsNull()
    {
        // Fewer than 2 CRLFs in this read - nothing can be trusted as complete.
        var raw = "+00000.5"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.Null(reading);
    }

    [Fact]
    public void ParseLatest_MalformedInteriorFrame_IsRejected_NotFabricated()
    {
        // Synthetic: a real device never sends this, but the parser must
        // reject anything that doesn't match the verified fixed format
        // rather than guessing at a partial/garbled value.
        var raw = "\r\ngarbage-not-a-frame\r\n"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.Null(reading);
    }

    [Fact]
    public void ParseLatest_MixOfValidAndMalformedInterior_UsesOnlyValidOnes()
    {
        var raw = "\r\n+00000.100 Kg\r\nnoise\r\n+00000.200 Kg\r\n"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(0.200m, reading!.Value); // the latest VALID frame, "noise" ignored
    }

    [Fact]
    public void ParseLatest_RawDataIsPreservedOnTheReading()
    {
        var raw = "\r\n+00000.100 Kg\r\n+00000.100 Kg\r\n"u8.ToArray();

        var reading = VideoconWeightFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Same(raw, reading!.RawData);
    }
}
