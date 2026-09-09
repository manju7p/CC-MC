using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

public sealed class DeviceConfigurationRepository(SqliteConnectionFactory connectionFactory) : IDeviceConfigurationRepository
{
    private const string SelectColumns = """
        SELECT id, kind, name, com_port, baud_rate, data_bits, stop_bits, parity, flow_control, read_timeout_ms, is_enabled
        FROM device_configurations
        """;

    public async Task<DeviceConfiguration?> GetAsync(DeviceKind kind, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE kind = $kind;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<DeviceConfiguration>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + ";";
        var results = new List<DeviceConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    public async Task UpsertAsync(DeviceConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_configurations
                (kind, name, com_port, baud_rate, data_bits, stop_bits, parity, flow_control, read_timeout_ms, is_enabled)
            VALUES
                ($kind, $name, $comPort, $baudRate, $dataBits, $stopBits, $parity, $flowControl, $readTimeoutMs, $isEnabled)
            ON CONFLICT (kind) DO UPDATE SET
                name = excluded.name,
                com_port = excluded.com_port,
                baud_rate = excluded.baud_rate,
                data_bits = excluded.data_bits,
                stop_bits = excluded.stop_bits,
                parity = excluded.parity,
                flow_control = excluded.flow_control,
                read_timeout_ms = excluded.read_timeout_ms,
                is_enabled = excluded.is_enabled;
            """;
        command.Parameters.AddWithValue("$kind", configuration.Kind.ToString());
        command.Parameters.AddWithValue("$name", configuration.Name);
        command.Parameters.AddWithValue("$comPort", configuration.Serial.ComPort);
        command.Parameters.AddWithValue("$baudRate", configuration.Serial.BaudRate);
        command.Parameters.AddWithValue("$dataBits", configuration.Serial.DataBits);
        command.Parameters.AddWithValue("$stopBits", configuration.Serial.StopBits.ToString());
        command.Parameters.AddWithValue("$parity", configuration.Serial.Parity.ToString());
        command.Parameters.AddWithValue("$flowControl", configuration.Serial.FlowControl.ToString());
        command.Parameters.AddWithValue("$readTimeoutMs", configuration.Serial.ReadTimeoutMs);
        command.Parameters.AddWithValue("$isEnabled", configuration.IsEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DeviceConfiguration Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Kind = Enum.Parse<DeviceKind>(reader.GetString(1)),
        Name = reader.GetString(2),
        Serial = new SerialConfiguration
        {
            ComPort = reader.GetString(3),
            BaudRate = reader.GetInt32(4),
            DataBits = reader.GetInt32(5),
            StopBits = Enum.Parse<SerialStopBits>(reader.GetString(6)),
            Parity = Enum.Parse<SerialParity>(reader.GetString(7)),
            FlowControl = Enum.Parse<SerialFlowControl>(reader.GetString(8)),
            ReadTimeoutMs = reader.GetInt32(9),
        },
        IsEnabled = reader.GetInt32(10) != 0,
    };
}
