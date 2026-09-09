using CCMC.Contracts.Auth;

namespace CCMC.Application.Abstractions;

/// <summary>
/// IsOffline distinguishes a real cloud-authenticated session from an
/// offline-verified one (see IOfflineCredentialStore). An offline session's
/// AccessToken is always empty - never a real bearer token, and never usable
/// for cloud calls - SyncEngineService must treat IsOffline the same as "no
/// session" for sync purposes (see its own doc comment).
/// </summary>
public sealed record Session(string AccessToken, AuthenticatedUserDto User, DateTimeOffset ObtainedAt, bool IsOffline = false);

/// <summary>Holds the current operator's session in memory for the app's lifetime.</summary>
public interface ISessionStore
{
    Session? Current { get; }
    void Set(Session session);
    void Clear();
}
