namespace CCMC.Cloud.Domain.Enums;

// Domain-owned enums (PascalCase, independent of wire format). CCMC.Contracts
// defines the SAME concepts as ALL_CAPS wire enums for JSON - the mapping
// between the two happens at the API boundary (controllers/mapping code),
// exactly mirroring how the Windows client's own Domain/Contracts split works.
// Kept deliberately identical in shape to the Windows client's domain enums
// (CCMC.Domain.Enums) since both sides model the same real-world concepts -
// this is not code reuse, there is no project reference between them.

public enum RecordStatus
{
    Active,
    Inactive,
}

/// <summary>
/// The system never auto-rejects (BRD v2 section 13/38's diagram: an
/// out-of-range reading goes to HOLD, a Manager then resolves it to ACCEPT
/// or REJECT). QualityValidationService only ever returns Accepted or Hold;
/// Rejected is reachable only through ReceptionService.Override.
/// </summary>
public enum TransactionStatus
{
    Accepted,
    Rejected,
    Hold,
}

public enum ReadingSource
{
    Manual,
    Device,
}

public enum QualityParameter
{
    Fat,
    Snf,
    Temperature,
}

/// <summary>BRD v5.0 section 25 (PREFS_RATE_TYPE) - see CCMC.Domain.Enums.RateFormulaType (Windows client) for the matching mirror.</summary>
public enum RateFormulaType
{
    FatVsSnf,
    TsBased,
}
