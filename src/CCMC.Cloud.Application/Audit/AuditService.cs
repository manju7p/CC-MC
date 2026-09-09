using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Infrastructure.Persistence;

namespace CCMC.Cloud.Application.Audit;

public sealed record AuditEntry(
    int? UserId,
    int? CentreId,
    string Action,
    string ResourceType,
    string ResourceId,
    string? OldValueJson = null,
    string? NewValueJson = null,
    string? Reason = null);

/// <summary>
/// Records an audit row into the SAME DbContext change tracker as the
/// caller's own work, WITHOUT calling SaveChangesAsync itself - the caller
/// (inside its own transaction) persists the audit entry atomically with the
/// business change it accompanies (e.g. reception creation + its audit row
/// commit or roll back together). A caller with no surrounding transaction
/// (e.g. login) must call SaveChangesAsync itself after RecordAsync. Never
/// logs passwords/tokens/secrets - only structural metadata.
/// </summary>
public interface IAuditService
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public sealed class AuditService(CcmcDbContext db) : IAuditService
{
    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = entry.UserId,
            CentreId = entry.CentreId,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            OldValueJson = entry.OldValueJson,
            NewValueJson = entry.NewValueJson,
            Reason = entry.Reason,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }
}
