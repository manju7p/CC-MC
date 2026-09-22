namespace CCMC.Cloud.Application.Common;

/// <summary>Maps to HTTP 400 - the request itself is invalid (references a nonexistent/inactive entity, etc.).</summary>
public sealed class ValidationException(string message) : Exception(message);

/// <summary>Maps to HTTP 404.</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>
/// Maps to HTTP 403 - the authenticated user is not authorized for the
/// target centre. Server-side enforcement only - never trusts the client
/// (BRD v2 section 14 / this session's "Centre Scoping" requirement).
/// A null <paramref name="centreId"/> means "the global (all-centres) default
/// configuration row" - only a user with CentreAccess.AllCentres may touch
/// that row, since it is not scoped to any single centre (see
/// RateFormulaSettingsService.UpsertAsync).
/// </summary>
public sealed class CentreAccessDeniedException(int? centreId) : Exception(
    centreId is { } id ? $"No access to centre {id}." : "Only a user with access to all centres may change the global (all-centres) default configuration.");

/// <summary>Maps to HTTP 409 - a localIdempotencyKey was reused with a genuinely different payload (a caller bug, not a legitimate retry).</summary>
public sealed class IdempotencyConflictException(string message, IReadOnlyList<string> conflictingFields, int existingId)
    : Exception(message)
{
    public IReadOnlyList<string> ConflictingFields { get; } = conflictingFields;
    public int ExistingId { get; } = existingId;
}

/// <summary>Maps to HTTP 409 - a business-rule conflict that is not an idempotency conflict (e.g. overriding a transaction no longer on HOLD).</summary>
public sealed class BusinessConflictException(string message) : Exception(message);
