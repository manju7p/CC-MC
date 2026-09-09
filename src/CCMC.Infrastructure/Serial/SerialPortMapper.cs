using System.IO.Ports;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;

namespace CCMC.Infrastructure.Serial;

/// <summary>Maps CCMC.Domain's serial enums (kept dependency-free from System.IO.Ports) to the real BCL types.</summary>
internal static class SerialPortMapper
{
    public static Parity ToParity(SerialParity parity) => parity switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(parity)),
    };

    public static StopBits ToStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => throw new ArgumentOutOfRangeException(nameof(stopBits)),
    };

    public static Handshake ToHandshake(SerialFlowControl flowControl) => flowControl switch
    {
        SerialFlowControl.None => Handshake.None,
        SerialFlowControl.XOnXOff => Handshake.XOnXOff,
        SerialFlowControl.RequestToSend => Handshake.RequestToSend,
        SerialFlowControl.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => throw new ArgumentOutOfRangeException(nameof(flowControl)),
    };

    public static void Apply(SerialPort port, SerialConfiguration config)
    {
        port.PortName = config.ComPort;
        port.BaudRate = config.BaudRate;
        port.DataBits = config.DataBits;
        port.StopBits = ToStopBits(config.StopBits);
        port.Parity = ToParity(config.Parity);
        port.Handshake = ToHandshake(config.FlowControl);
        port.ReadTimeout = config.ReadTimeoutMs;
        port.WriteTimeout = config.ReadTimeoutMs;
    }
}
