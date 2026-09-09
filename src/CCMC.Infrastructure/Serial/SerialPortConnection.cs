using System.IO.Ports;
using CCMC.Domain.ValueObjects;

namespace CCMC.Infrastructure.Serial;

/// <summary>
/// The single real owner of one open COM port. Only ever constructed by
/// SerialConnectionManager.Acquire(), which enforces "exactly one owner per
/// port" - never construct this directly.
/// </summary>
public sealed class SerialPortConnection : IDisposable
{
    private readonly SerialPort _port;

    internal SerialPortConnection(SerialConfiguration configuration)
    {
        Configuration = configuration;
        _port = new SerialPort();
        SerialPortMapper.Apply(_port, configuration);
    }

    public SerialConfiguration Configuration { get; }
    public bool IsOpen => _port.IsOpen;

    /// <summary>
    /// Snapshot of the ACTUAL runtime System.IO.Ports.SerialPort properties
    /// (not just the configuration that was applied) - added for the live
    /// weighing-scale hardware investigation (see CLAUDE.md "Hardware
    /// Verification") to rule out a runtime/configuration mismatch (e.g.
    /// Handshake, DtrEnable, RtsEnable, Encoding) as an explanation for why
    /// the application could fail to receive what another serial terminal
    /// receives. Diagnostic only - never used for control flow.
    /// </summary>
    public string DescribeRuntimeSettings() =>
        $"PortName={_port.PortName} BaudRate={_port.BaudRate} DataBits={_port.DataBits} " +
        $"Parity={_port.Parity} StopBits={_port.StopBits} Handshake={_port.Handshake} " +
        $"ReadTimeout={_port.ReadTimeout} WriteTimeout={_port.WriteTimeout} " +
        $"Encoding={_port.Encoding.WebName} NewLine={Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(_port.NewLine))} " +
        $"DtrEnable={_port.DtrEnable} RtsEnable={_port.RtsEnable} " +
        $"ReadBufferSize={_port.ReadBufferSize} WriteBufferSize={_port.WriteBufferSize}";

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => _port.Open(), cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            if (_port.IsOpen) _port.Close();
        },
        cancellationToken);

    /// <summary>
    /// Reads whatever bytes arrive within the given window - not framed,
    /// not parsed. Returns an empty array if nothing arrived (this is the
    /// normal "device connected but silent" case, not an error).
    /// </summary>
    public async Task<byte[]> ReadAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_port.IsOpen)
        {
            throw new InvalidOperationException($"Port '{Configuration.ComPort}' is not open.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var buffer = new byte[4096];
        try
        {
            var read = await _port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token);
            return buffer[..read];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout fired, not the caller's own cancellation - a plain "nothing arrived", not an error.
            return [];
        }
        catch (IOException)
        {
            // SerialPort surfaces a stalled/disconnected device as an IOException on the underlying stream.
            return [];
        }
    }

    public void Dispose()
    {
        if (_port.IsOpen)
        {
            _port.Close();
        }
        _port.Dispose();
    }
}
