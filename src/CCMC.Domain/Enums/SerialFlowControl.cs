namespace CCMC.Domain.Enums;

/// <summary>Domain-owned mirror of System.IO.Ports.Handshake - see SerialParity.</summary>
public enum SerialFlowControl
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff,
}
