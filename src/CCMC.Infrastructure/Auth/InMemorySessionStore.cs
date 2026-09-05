using CCMC.Application.Abstractions;

namespace CCMC.Infrastructure.Auth;

/// <summary>
/// Holds the current operator's session for the app's process lifetime only
/// - not persisted to disk. Offline re-login across an app restart is
/// intentionally not supported yet (see CLAUDE.md "Decisions Pending #2" -
/// no offline authentication design exists).
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private Session? _current;

    public Session? Current => _current;

    public void Set(Session session) => _current = session;

    public void Clear() => _current = null;
}
