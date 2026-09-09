namespace CCMC.Domain.Enums;

/// <summary>
/// Domain-owned mirror of System.IO.Ports.Parity so CCMC.Domain has no dependency
/// on a serial-I/O package. CCMC.Infrastructure maps this to the real
/// System.IO.Ports enum when it actually opens a port.
/// </summary>
public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}
