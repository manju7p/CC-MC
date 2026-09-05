using System.Collections.Concurrent;
using CCMC.Domain.ValueObjects;

namespace CCMC.Infrastructure.Serial;

/// <summary>
/// The single point that hands out COM port ownership for the whole app.
/// Enforces "exactly one owner per physical COM port" (BRD v2 Technical
/// Design section 11) - Acquire() throws SerialPortOwnershipException if the
/// requested port is already owned by an active connection.
/// </summary>
public sealed class SerialConnectionManager
{
    private readonly ConcurrentDictionary<string, SerialPortConnection> _owners = new(StringComparer.OrdinalIgnoreCase);

    public SerialPortConnection Acquire(SerialConfiguration configuration)
    {
        var connection = new SerialPortConnection(configuration);
        if (!_owners.TryAdd(configuration.ComPort, connection))
        {
            connection.Dispose();
            throw new SerialPortOwnershipException(configuration.ComPort);
        }
        return connection;
    }

    public void Release(string comPort)
    {
        if (_owners.TryRemove(comPort, out var connection))
        {
            connection.Dispose();
        }
    }

    public bool IsOwned(string comPort) => _owners.ContainsKey(comPort);
}
