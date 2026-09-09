namespace CCMC.Infrastructure.Serial;

/// <summary>
/// Thrown when a second caller tries to acquire ownership of a COM port
/// already owned by another connection - "Exactly ONE component must own
/// each physical COM port" (BRD v2 Technical Design section 11 / this
/// session's Serial Infrastructure requirements). This is not a transient/
/// retryable condition - it is a programming error in how the app wired up
/// its devices.
/// </summary>
public sealed class SerialPortOwnershipException(string portName)
    : Exception($"COM port '{portName}' is already owned by another connection. Exactly one component must own a physical COM port.")
{
    public string PortName { get; } = portName;
}
