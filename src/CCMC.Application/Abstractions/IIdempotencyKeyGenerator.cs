namespace CCMC.Application.Abstractions;

/// <summary>
/// Generates a stable local idempotency key, called exactly once per captured
/// reception, never regenerated on a sync retry (see context.md "Synchronization Model").
/// </summary>
public interface IIdempotencyKeyGenerator
{
    string NewKey();
}
