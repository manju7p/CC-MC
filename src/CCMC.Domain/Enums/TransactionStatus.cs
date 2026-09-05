namespace CCMC.Domain.Enums;

/// <summary>
/// The system never auto-rejects (see docs/assumptions.md #auto-reject-vs-hold,
/// documented from the cloud API's own MVP decision). Rejected is reachable only
/// through a Manager override of a Hold transaction.
/// </summary>
public enum TransactionStatus
{
    Accepted,
    Rejected,
    Hold,
}
