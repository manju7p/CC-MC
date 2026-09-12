using System.Text.Json.Serialization;
using CCMC.Contracts.Enums;
using CCMC.Contracts.Json;

namespace CCMC.Contracts.Dtos;

/// <summary>
/// Mirrors the cloud API's CreateReceptionRequest exactly (verified during
/// Phase 0 - see context.md "Cloud Responsibilities"). LocalIdempotencyKey is
/// the additive, optional field the cloud already supports for idempotent
/// retries - this is what makes the Windows app's sync engine safe.
/// </summary>
public sealed class CreateReceptionRequestDto
{
    [JsonPropertyName("centreId")] public required int CentreId { get; init; }
    [JsonPropertyName("sourceId")] public required int SourceId { get; init; }
    [JsonPropertyName("vehicleId")] public required int VehicleId { get; init; }
    [JsonPropertyName("quantityKg")] public required decimal QuantityKg { get; init; }
    [JsonPropertyName("fat")] public required decimal Fat { get; init; }
    [JsonPropertyName("snf")] public required decimal Snf { get; init; }
    [JsonPropertyName("temperature")] public required decimal Temperature { get; init; }

    /// <summary>
    /// The operator's explicit ACCEPT/HOLD decision at capture time (never
    /// REJECTED here - see OverrideReceptionRequestDto/CLAUDE.md). Optional
    /// for backward compatibility with any caller that predates this field:
    /// when omitted, the cloud falls back to its own automatic quality-rule
    /// suggestion exactly as before (see ReceptionService.CreateAsync).
    /// </summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransactionStatus? Status { get; init; }

    /// <summary>Milk analyser fields beyond Fat/Snf - additive, optional (see MilkReceptionTransaction's doc comment).</summary>
    [JsonPropertyName("clr"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Clr { get; init; }

    [JsonPropertyName("water"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Water { get; init; }

    [JsonPropertyName("protein"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Protein { get; init; }

    /// <summary>The exact analyser payload these values were decoded from (device or manual test input) - preserved verbatim.</summary>
    [JsonPropertyName("rawAnalyserPayload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawAnalyserPayload { get; init; }

    [JsonPropertyName("localIdempotencyKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalIdempotencyKey { get; init; }
}

/// <summary>
/// Mirrors the cloud's MilkReceptionTransaction response shape, plus the
/// additive `outcome` field ("created"/"duplicate") the idempotent-create
/// path returns - see context.md "Cloud Responsibilities" for the full
/// created/duplicate/409 contract this drives.
/// </summary>
public sealed class ReceptionTransactionDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("transactionNumber")] public required string TransactionNumber { get; init; }
    [JsonPropertyName("centreId")] public required int CentreId { get; init; }
    [JsonPropertyName("sourceId")] public required int SourceId { get; init; }
    [JsonPropertyName("vehicleId")] public required int VehicleId { get; init; }
    [JsonPropertyName("operatorUserId")] public required int OperatorUserId { get; init; }

    [JsonPropertyName("quantityKg"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal QuantityKg { get; init; }

    [JsonPropertyName("fat"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal Fat { get; init; }

    [JsonPropertyName("snf"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal Snf { get; init; }

    [JsonPropertyName("temperature"), JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public required decimal Temperature { get; init; }

    [JsonPropertyName("clr"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    public decimal? Clr { get; init; }

    [JsonPropertyName("water"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    public decimal? Water { get; init; }

    [JsonPropertyName("protein"), JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))]
    public decimal? Protein { get; init; }

    [JsonPropertyName("rawAnalyserPayload")] public string? RawAnalyserPayload { get; init; }

    [JsonPropertyName("status")] public required TransactionStatus Status { get; init; }
    [JsonPropertyName("readingSource")] public required ReadingSource ReadingSource { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("receivedAt")] public required string ReceivedAt { get; init; }

    /// <summary>"created" or "duplicate" - both are success. Absent on non-idempotent responses.</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
}

public sealed class OverrideReceptionRequestDto
{
    [JsonPropertyName("newStatus")] public required TransactionStatus NewStatus { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

/// <summary>
/// Shape of the cloud's 409 Conflict body when a localIdempotencyKey is
/// reused with a genuinely different payload (a caller bug, not a legitimate
/// retry) - see context.md "Cloud Responsibilities".
/// </summary>
public sealed class ReceptionConflictResponseDto
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("conflictingFields")] public List<string>? ConflictingFields { get; init; }
    [JsonPropertyName("existingTransactionId")] public int? ExistingTransactionId { get; init; }
}
