namespace CCMC.Cloud.Application.Auth;

public sealed record CentreAccess(bool AllCentres, IReadOnlyList<int> CentreIds);

/// <summary>
/// The authenticated caller's resolved identity for the current request -
/// loaded fresh from the database on every request (see UserContextService),
/// never cached in the JWT. This matches the design the Windows client's own
/// documentation already assumes (context.md "Authentication / Authorization" -
/// "RequestUser is loaded fresh per request, not cached in the token"), so a
/// permission/role/centre-access change takes effect on the user's very next
/// request rather than only at next login.
/// </summary>
public sealed record RequestUser(
    int Id,
    string Email,
    string FullName,
    IReadOnlyList<string> RoleNames,
    IReadOnlyList<string> PermissionCodes,
    CentreAccess CentreAccess);

/// <summary>
/// Server-authoritative centre-scope enforcement (BRD v2 section 14 / this
/// session's "Centre Scoping" requirement) - a small static guard, not a
/// generic policy engine, so the check is visible at every call site rather
/// than hidden behind indirection.
/// </summary>
public static class CentreAccessGuard
{
    public static void AssertCanAccess(CentreAccess access, int centreId)
    {
        if (access.AllCentres) return;
        if (access.CentreIds.Contains(centreId)) return;
        throw new Common.CentreAccessDeniedException(centreId);
    }
}
