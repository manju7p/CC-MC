using System.Text.Json.Serialization;

namespace CCMC.Contracts.Dtos;

public sealed class DashboardSummaryDto
{
    [JsonPropertyName("date")] public required string Date { get; init; }
    [JsonPropertyName("centreIds")] public required List<int> CentreIds { get; init; }
    [JsonPropertyName("totalTransactions")] public required int TotalTransactions { get; init; }
    [JsonPropertyName("accepted")] public required int Accepted { get; init; }
    [JsonPropertyName("rejected")] public required int Rejected { get; init; }
    [JsonPropertyName("hold")] public required int Hold { get; init; }
}

public sealed class AuditLogDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("userId")] public int? UserId { get; init; }
    [JsonPropertyName("centreId")] public int? CentreId { get; init; }
    [JsonPropertyName("action")] public required string Action { get; init; }
    [JsonPropertyName("resourceType")] public required string ResourceType { get; init; }
    [JsonPropertyName("resourceId")] public required string ResourceId { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("createdAt")] public required string CreatedAt { get; init; }
}
