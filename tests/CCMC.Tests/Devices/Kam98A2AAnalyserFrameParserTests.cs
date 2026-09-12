using CCMC.Domain.Devices;
using CCMC.Infrastructure.Devices;
using Xunit;

namespace CCMC.Tests.Devices;

/// <summary>
/// Both "real sample" fixtures below are the exact two device outputs
/// supplied directly by the project owner as real, observed Ekomilk Milkana
/// KAM98-2A output (not invented, not from manufacturer documentation - see
/// Kam98A2AAnalyserFrameParser's doc comment for the derivation). Malformed
/// fixtures are synthetic by necessity, used only to prove rejection logic
/// never fabricates a reading.
/// </summary>
public class Kam98A2AAnalyserFrameParserTests
{
    private const string DeviceId = "milk-analyser-ekomilk-kam98-2a";
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);

    private const string Sample1 = "(03900830283801210000032404503)";
    private const string Sample2 = "(02500520171638900000020906585)";

    [Fact]
    public void ParsePayload_RealSample1_DecodesAllFieldsExactly()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParsePayload(Sample1, DeviceId, Now);

        Assert.Equal(3.9m, reading.Fat);
        Assert.Equal(8.3m, reading.Snf);
        Assert.Equal(28.4m, reading.Clr);
        Assert.Null(reading.Temperature); // KAM98-2A does not measure temperature - never fabricated
        Assert.Equal(1.21m, reading.OptionalParameters!["Water"]);
        Assert.Equal(3.24m, reading.OptionalParameters!["Protein"]);
        Assert.Equal(DeviceId, reading.DeviceId);
        Assert.Equal(Now, reading.Timestamp);
    }

    [Fact]
    public void ParsePayload_RealSample2_DecodesAllFieldsExactly()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParsePayload(Sample2, DeviceId, Now);

        Assert.Equal(2.5m, reading.Fat);
        Assert.Equal(5.2m, reading.Snf);
        Assert.Equal(17.2m, reading.Clr);
        Assert.Null(reading.Temperature);
        Assert.Equal(38.9m, reading.OptionalParameters!["Water"]);
        Assert.Equal(2.09m, reading.OptionalParameters!["Protein"]);
    }

    [Fact]
    public void ParsePayload_WithoutParentheses_StillDecodes()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParsePayload("03900830283801210000032404503", DeviceId, Now);

        Assert.Equal(3.9m, reading.Fat);
        Assert.Equal(8.3m, reading.Snf);
    }

    [Fact]
    public void ParsePayload_WithSurroundingWhitespaceAndCrLf_StillDecodes()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParsePayload("  \r\n(03900830283801210000032404503)\r\n  ", DeviceId, Now);

        Assert.Equal(3.9m, reading.Fat);
        Assert.Equal(28.4m, reading.Clr);
    }

    [Fact]
    public void ParsePayload_RawDataIsPreservedOnTheReading()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParsePayload(Sample1, DeviceId, Now);

        Assert.NotNull(reading.RawData);
        Assert.Equal(Sample1, System.Text.Encoding.ASCII.GetString(reading.RawData!));
    }

    [Fact]
    public void ParsePayload_TooShort_ThrowsDeviceParseException()
    {
        var ex = Assert.Throws<DeviceParseException>(
            () => Kam98A2AAnalyserFrameParser.ParsePayload("(039008302838012100000324045)", DeviceId, Now));

        Assert.Equal(DeviceId, ex.DeviceId);
    }

    [Fact]
    public void ParsePayload_TooLong_ThrowsDeviceParseException()
    {
        Assert.Throws<DeviceParseException>(
            () => Kam98A2AAnalyserFrameParser.ParsePayload("(039008302838012100000324045033)", DeviceId, Now));
    }

    [Fact]
    public void ParsePayload_NonNumericCharacter_ThrowsDeviceParseException()
    {
        Assert.Throws<DeviceParseException>(
            () => Kam98A2AAnalyserFrameParser.ParsePayload("(0390083028380121000003240450X)", DeviceId, Now));
    }

    [Fact]
    public void ParsePayload_EmptyString_ThrowsDeviceParseException()
    {
        Assert.Throws<DeviceParseException>(() => Kam98A2AAnalyserFrameParser.ParsePayload("", DeviceId, Now));
    }

    [Fact]
    public void ParsePayload_UnexpectedCharactersMixedIn_ThrowsDeviceParseException()
    {
        Assert.Throws<DeviceParseException>(
            () => Kam98A2AAnalyserFrameParser.ParsePayload("garbage-not-a-frame", DeviceId, Now));
    }

    [Fact]
    public void ParseLatest_EmptyRead_ReturnsNull_NeverThrows()
    {
        var reading = Kam98A2AAnalyserFrameParser.ParseLatest([], DeviceId, Now);

        Assert.Null(reading);
    }

    [Fact]
    public void ParseLatest_NoCompleteFrameInBuffer_ReturnsNull()
    {
        var raw = "(0390083028"u8.ToArray(); // truncated - never a complete frame

        var reading = Kam98A2AAnalyserFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.Null(reading);
    }

    [Fact]
    public void ParseLatest_RealSampleFrameInByteStream_Decodes()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(Sample1 + "\r\n");

        var reading = Kam98A2AAnalyserFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(3.9m, reading!.Fat);
        Assert.Equal(28.4m, reading.Clr);
    }

    [Fact]
    public void ParseLatest_MultipleFramesInOneRead_UsesLatest()
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(Sample2 + Sample1); // sample1 decoded last

        var reading = Kam98A2AAnalyserFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.NotNull(reading);
        Assert.Equal(3.9m, reading!.Fat); // sample1's Fat, not sample2's
    }

    [Fact]
    public void ParseLatest_MalformedFrameInBuffer_IsRejected_NotFabricated()
    {
        var raw = "(not-a-valid-frame-at-all)"u8.ToArray();

        var reading = Kam98A2AAnalyserFrameParser.ParseLatest(raw, DeviceId, Now);

        Assert.Null(reading);
    }
}
