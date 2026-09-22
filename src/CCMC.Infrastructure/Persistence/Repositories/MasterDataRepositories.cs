using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using Microsoft.Data.Sqlite;

namespace CCMC.Infrastructure.Persistence.Repositories;

/// <summary>
/// Master-data repositories are simple "replace all" caches: the cloud is
/// authoritative, so on each successful MasterDataSyncService.PullAsync() the
/// local table is cleared and rewritten from the cloud's current list, inside
/// one transaction. Between pulls (i.e. while offline), whatever was last
/// pulled remains available for reception.
/// </summary>
public sealed class ChillingCentreRepository(SqliteConnectionFactory connectionFactory) : IChillingCentreRepository
{
    public async Task<IReadOnlyList<ChillingCentre>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, code, name, status FROM chilling_centres ORDER BY name;";
        var results = new List<ChillingCentre>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChillingCentre
            {
                Id = reader.GetInt32(0),
                Code = reader.GetString(1),
                Name = reader.GetString(2),
                Status = Enum.Parse<RecordStatus>(reader.GetString(3)),
            });
        }
        return results;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<ChillingCentre> centres, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM chilling_centres;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var centre in centres)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO chilling_centres (id, code, name, status) VALUES ($id, $code, $name, $status);";
            insert.Parameters.AddWithValue("$id", centre.Id);
            insert.Parameters.AddWithValue("$code", centre.Code);
            insert.Parameters.AddWithValue("$name", centre.Name);
            insert.Parameters.AddWithValue("$status", centre.Status.ToString());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }
}

public sealed class SourceRepository(SqliteConnectionFactory connectionFactory) : ISourceRepository
{
    public async Task<IReadOnlyList<Source>> ListByCentreAsync(int centreId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE centre_id = $centreId ORDER BY name;";
        command.Parameters.AddWithValue("$centreId", centreId);
        var results = new List<Source>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    public async Task<Source?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<Source> sources, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM sources;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var source in sources)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO sources (id, code, name, location, contact, milk_type, status, centre_id)
                VALUES ($id, $code, $name, $location, $contact, $milkType, $status, $centreId);
                """;
            insert.Parameters.AddWithValue("$id", source.Id);
            insert.Parameters.AddWithValue("$code", source.Code);
            insert.Parameters.AddWithValue("$name", source.Name);
            insert.Parameters.AddWithValue("$location", (object?)source.Location ?? DBNull.Value);
            insert.Parameters.AddWithValue("$contact", (object?)source.Contact ?? DBNull.Value);
            insert.Parameters.AddWithValue("$milkType", (object?)source.MilkType ?? DBNull.Value);
            insert.Parameters.AddWithValue("$status", source.Status.ToString());
            insert.Parameters.AddWithValue("$centreId", source.CentreId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private const string SelectColumns = "SELECT id, code, name, location, contact, milk_type, status, centre_id FROM sources";

    private static Source Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Code = reader.GetString(1),
        Name = reader.GetString(2),
        Location = reader.IsDBNull(3) ? null : reader.GetString(3),
        Contact = reader.IsDBNull(4) ? null : reader.GetString(4),
        MilkType = reader.IsDBNull(5) ? null : reader.GetString(5),
        Status = Enum.Parse<RecordStatus>(reader.GetString(6)),
        CentreId = reader.GetInt32(7),
    };
}

public sealed class VehicleRepository(SqliteConnectionFactory connectionFactory) : IVehicleRepository
{
    public async Task<IReadOnlyList<Vehicle>> ListByCentreAsync(int centreId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE centre_id = $centreId ORDER BY vehicle_number;";
        command.Parameters.AddWithValue("$centreId", centreId);
        var results = new List<Vehicle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    public async Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<Vehicle> vehicles, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM vehicles;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var vehicle in vehicles)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO vehicles (id, vehicle_number, tanker_number, driver_name, driver_mobile, capacity_kg, status, centre_id)
                VALUES ($id, $vehicleNumber, $tankerNumber, $driverName, $driverMobile, $capacityKg, $status, $centreId);
                """;
            insert.Parameters.AddWithValue("$id", vehicle.Id);
            insert.Parameters.AddWithValue("$vehicleNumber", vehicle.VehicleNumber);
            insert.Parameters.AddWithValue("$tankerNumber", (object?)vehicle.TankerNumber ?? DBNull.Value);
            insert.Parameters.AddWithValue("$driverName", (object?)vehicle.DriverName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$driverMobile", (object?)vehicle.DriverMobile ?? DBNull.Value);
            insert.Parameters.AddWithValue("$capacityKg", (object?)vehicle.CapacityKg ?? DBNull.Value);
            insert.Parameters.AddWithValue("$status", vehicle.Status.ToString());
            insert.Parameters.AddWithValue("$centreId", vehicle.CentreId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private const string SelectColumns =
        "SELECT id, vehicle_number, tanker_number, driver_name, driver_mobile, capacity_kg, status, centre_id FROM vehicles";

    private static Vehicle Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        VehicleNumber = reader.GetString(1),
        TankerNumber = reader.IsDBNull(2) ? null : reader.GetString(2),
        DriverName = reader.IsDBNull(3) ? null : reader.GetString(3),
        DriverMobile = reader.IsDBNull(4) ? null : reader.GetString(4),
        CapacityKg = reader.IsDBNull(5) ? null : (decimal)reader.GetDouble(5),
        Status = Enum.Parse<RecordStatus>(reader.GetString(6)),
        CentreId = reader.GetInt32(7),
    };
}

public sealed class QualityRuleRepository(SqliteConnectionFactory connectionFactory) : IQualityRuleRepository
{
    public async Task<IReadOnlyList<QualityRule>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + ";";
        var results = new List<QualityRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<QualityRule> rules, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM quality_rules;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var rule in rules)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO quality_rules (id, parameter, min_value, max_value, centre_id)
                VALUES ($id, $parameter, $min, $max, $centreId);
                """;
            insert.Parameters.AddWithValue("$id", rule.Id);
            insert.Parameters.AddWithValue("$parameter", rule.Parameter.ToString());
            insert.Parameters.AddWithValue("$min", rule.MinValue);
            insert.Parameters.AddWithValue("$max", rule.MaxValue);
            insert.Parameters.AddWithValue("$centreId", (object?)rule.CentreId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<QualityParameter, QualityRule>> ResolveForCentreAsync(
        int centreId, IReadOnlyList<QualityParameter> parameters, CancellationToken cancellationToken)
    {
        var all = await ListAsync(cancellationToken);
        var result = new Dictionary<QualityParameter, QualityRule>();

        foreach (var parameter in parameters)
        {
            // Centre-specific overrides global, mirroring the cloud's own resolution rule (context.md).
            var rule = all.FirstOrDefault(r => r.Parameter == parameter && r.CentreId == centreId)
                       ?? all.FirstOrDefault(r => r.Parameter == parameter && r.CentreId is null);
            if (rule is not null)
            {
                result[parameter] = rule;
            }
        }

        return result;
    }

    private const string SelectColumns = "SELECT id, parameter, min_value, max_value, centre_id FROM quality_rules";

    private static QualityRule Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Parameter = Enum.Parse<QualityParameter>(reader.GetString(1)),
        MinValue = (decimal)reader.GetDouble(2),
        MaxValue = (decimal)reader.GetDouble(3),
        CentreId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
    };
}

public sealed class RateFormulaSettingsRepository(SqliteConnectionFactory connectionFactory) : IRateFormulaSettingsRepository
{
    public async Task<IReadOnlyList<RateFormulaSettings>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + ";";
        var results = new List<RateFormulaSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(Map(reader));
        return results;
    }

    public async Task ReplaceAllAsync(IReadOnlyList<RateFormulaSettings> settings, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.Open();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM rate_formula_settings;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var setting in settings)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO rate_formula_settings (id, rate_type, value1, value2, ts_rate, centre_id)
                VALUES ($id, $rateType, $value1, $value2, $tsRate, $centreId);
                """;
            insert.Parameters.AddWithValue("$id", setting.Id);
            insert.Parameters.AddWithValue("$rateType", setting.RateType.ToString());
            insert.Parameters.AddWithValue("$value1", (object?)setting.Value1 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$value2", (object?)setting.Value2 ?? DBNull.Value);
            insert.Parameters.AddWithValue("$tsRate", (object?)setting.TsRate ?? DBNull.Value);
            insert.Parameters.AddWithValue("$centreId", (object?)setting.CentreId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<RateFormulaSettings?> ResolveForCentreAsync(int centreId, CancellationToken cancellationToken)
    {
        var all = await ListAsync(cancellationToken);

        // Centre-specific overrides global, mirroring QualityRuleRepository.ResolveForCentreAsync.
        return all.FirstOrDefault(s => s.CentreId == centreId)
            ?? all.FirstOrDefault(s => s.CentreId is null);
    }

    private const string SelectColumns = "SELECT id, rate_type, value1, value2, ts_rate, centre_id FROM rate_formula_settings";

    private static RateFormulaSettings Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        RateType = Enum.Parse<RateFormulaType>(reader.GetString(1)),
        Value1 = reader.IsDBNull(2) ? null : (decimal)reader.GetDouble(2),
        Value2 = reader.IsDBNull(3) ? null : (decimal)reader.GetDouble(3),
        TsRate = reader.IsDBNull(4) ? null : (decimal)reader.GetDouble(4),
        CentreId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
    };
}
