using System.Text.Json.Serialization;

namespace CCMC.Contracts.Enums;

// Wire-format enums: member names match the cloud API's JSON string values
// EXACTLY (verified during Phase 0 inspection of the existing NestJS API's
// @cc-mc/shared-types package - see context.md "Cloud Responsibilities").
// Do not rename members - they are serialized/deserialized by name via
// JsonStringEnumConverter, not by a separate mapping table.
//
// These are intentionally separate from CCMC.Domain's enums: CCMC.Contracts
// has zero project references (so it stays freely shareable), and the wire
// format is a distinct concern from internal domain representation. Mapping
// between the two happens at the CCMC.Application boundary.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecordStatus
{
    ACTIVE,
    INACTIVE,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransactionStatus
{
    ACCEPTED,
    REJECTED,
    HOLD,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadingSource
{
    MANUAL,
    DEVICE,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityParameter
{
    FAT,
    SNF,
    TEMPERATURE,
}
