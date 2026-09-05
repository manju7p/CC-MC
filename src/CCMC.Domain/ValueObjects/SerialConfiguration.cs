using CCMC.Domain.Enums;

namespace CCMC.Domain.ValueObjects;

/// <summary>
/// Every serial parameter a physical device connection needs. Never hard-code
/// these for a specific device in business logic - always route through a
/// configured instance of this type (BRD v2 Technical Design section 6 /
/// Engineering Rule "Device configuration must not be hard-coded").
/// </summary>
public sealed record SerialConfiguration
{
    public required string ComPort { get; init; }
    public required int BaudRate { get; init; }
    public int DataBits { get; init; } = 8;
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    public SerialParity Parity { get; init; } = SerialParity.None;
    public SerialFlowControl FlowControl { get; init; } = SerialFlowControl.None;
    public int ReadTimeoutMs { get; init; } = 3000;

    /// <summary>
    /// The physically verified weighing-scale configuration (this session, and
    /// BRD v2 section 5.1 / Technical Design section 6). This is a default/seed
    /// value for the weighing-scale device configuration row, not a constant
    /// business logic may hard-code elsewhere - see CLAUDE.md "Hardware
    /// Verification" for the discrepancy against an earlier, different
    /// "CEO-confirmed" configuration recorded for different equipment, which
    /// this value intentionally does NOT try to reconcile.
    /// </summary>
    public static SerialConfiguration VerifiedWeighingScaleDefault { get; } = new()
    {
        ComPort = "COM4",
        BaudRate = 2400,
        DataBits = 8,
        StopBits = SerialStopBits.One,
        Parity = SerialParity.None,
        FlowControl = SerialFlowControl.None,
        ReadTimeoutMs = 3000,
    };
}
