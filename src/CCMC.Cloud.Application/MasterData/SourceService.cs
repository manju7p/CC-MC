using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.MasterData;

public sealed record CreateSourceCommand(string Code, string Name, string? Location, string? Contact, string? MilkType, int CentreId);
public sealed record UpdateSourceCommand(string? Name, string? Location, string? Contact, string? MilkType, RecordStatus? Status);

public sealed class SourceService(CcmcDbContext db)
{
    public async Task<IReadOnlyList<Source>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var query = db.Sources.AsNoTracking().AsQueryable();
        if (!user.CentreAccess.AllCentres)
        {
            if (user.CentreAccess.CentreIds.Count == 0) return [];
            query = query.Where(s => user.CentreAccess.CentreIds.Contains(s.CentreId));
        }
        return await query.OrderBy(s => s.Name).ToListAsync(cancellationToken);
    }

    public async Task<Source> GetByIdAsync(RequestUser user, int id, CancellationToken cancellationToken)
    {
        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Source {id} not found.");
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, source.CentreId);
        return source;
    }

    public async Task<Source> CreateAsync(RequestUser user, CreateSourceCommand cmd, CancellationToken cancellationToken)
    {
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, cmd.CentreId);

        var centreExists = await db.ChillingCentres.AnyAsync(c => c.Id == cmd.CentreId, cancellationToken);
        if (!centreExists) throw new ValidationException("Centre not found.");

        var now = DateTimeOffset.UtcNow;
        var source = new Source
        {
            Code = cmd.Code, Name = cmd.Name, Location = cmd.Location, Contact = cmd.Contact, MilkType = cmd.MilkType,
            CentreId = cmd.CentreId, Status = RecordStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Sources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        return source;
    }

    public async Task<Source> UpdateAsync(RequestUser user, int id, UpdateSourceCommand cmd, CancellationToken cancellationToken)
    {
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Source {id} not found.");
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, source.CentreId);

        if (cmd.Name is not null) source.Name = cmd.Name;
        if (cmd.Location is not null) source.Location = cmd.Location;
        if (cmd.Contact is not null) source.Contact = cmd.Contact;
        if (cmd.MilkType is not null) source.MilkType = cmd.MilkType;
        if (cmd.Status is not null) source.Status = cmd.Status.Value;
        source.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return source;
    }
}
