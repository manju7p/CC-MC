using System.Text.Json.Serialization;
using CCMC.Contracts.Enums;
using CCMC.Contracts.Json;

namespace CCMC.Contracts.Dtos;

public sealed class ChillingCentreDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("status")] public required RecordStatus Status { get; init; }
}

public sealed class SourceDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("contact")] public string? Contact { get; init; }
    [JsonPropertyName("milkType")] public string? MilkType { get; init; }
    [JsonPropertyName("status")] public required RecordStatus Status { get; init; }
    [JsonPropertyName("centreId")] public required int CentreId { get; init; }
}

public sealed class VehicleDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("vehicleNumber")] public required string VehicleNumber { get; init; }
    [JsonPropertyName("tankerNumber")] public string? TankerNumber { get; init; }
    [JsonPropertyName("driverName")] public string? DriverName { get; init; }
    [JsonPropertyName("driverMobile")] public string? DriverMobile { get; init; }
    [JsonPropertyName("capacityKg"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    public decimal? CapacityKg { get; init; }
    [JsonPropertyName("status")] public required RecordStatus Status { get; init; }
    [JsonPropertyName("centreId")] public required int CentreId { get; init; }
}

public sealed class QualityRuleDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("parameter")] public required QualityParameter Parameter { get; init; }
    [JsonPropertyName("minValue"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal MinValue { get; init; }

    [JsonPropertyName("maxValue"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal MaxValue { get; init; }
    [JsonPropertyName("centreId")] public int? CentreId { get; init; }
}
