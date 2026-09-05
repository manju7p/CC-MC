using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Common;
using CCMC.Cloud.Domain.Entities;
using CCMC.Cloud.Domain.Enums;
using CCMC.Cloud.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CCMC.Cloud.Application.MasterData;

public sealed record CreateVehicleCommand(string VehicleNumber, string? TankerNumber, string? DriverName, string? DriverMobile, decimal? CapacityKg, int CentreId);
public sealed record UpdateVehicleCommand(string? TankerNumber, string? DriverName, string? DriverMobile, decimal? CapacityKg, RecordStatus? Status);

public sealed class VehicleService(CcmcDbContext db)
{
    public async Task<IReadOnlyList<Vehicle>> ListAsync(RequestUser user, CancellationToken cancellationToken)
    {
        var query = db.Vehicles.AsNoTracking().AsQueryable();
        if (!user.CentreAccess.AllCentres)
        {
            if (user.CentreAccess.CentreIds.Count == 0) return [];
            query = query.Where(v => user.CentreAccess.CentreIds.Contains(v.CentreId));
        }
        return await query.OrderBy(v => v.VehicleNumber).ToListAsync(cancellationToken);
    }

    public async Task<Vehicle> GetByIdAsync(RequestUser user, int id, CancellationToken cancellationToken)
    {
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {id} not found.");
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, vehicle.CentreId);
        return vehicle;
    }

    public async Task<Vehicle> CreateAsync(RequestUser user, CreateVehicleCommand cmd, CancellationToken cancellationToken)
    {
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, cmd.CentreId);

        var centreExists = await db.ChillingCentres.AnyAsync(c => c.Id == cmd.CentreId, cancellationToken);
        if (!centreExists) throw new ValidationException("Centre not found.");

        var now = DateTimeOffset.UtcNow;
        var vehicle = new Vehicle
        {
            VehicleNumber = cmd.VehicleNumber, TankerNumber = cmd.TankerNumber, DriverName = cmd.DriverName,
            DriverMobile = cmd.DriverMobile, CapacityKg = cmd.CapacityKg, CentreId = cmd.CentreId,
            Status = RecordStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(cancellationToken);
        return vehicle;
    }

    public async Task<Vehicle> UpdateAsync(RequestUser user, int id, UpdateVehicleCommand cmd, CancellationToken cancellationToken)
    {
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(v => v.Id == id, cancellationToken)
            ?? throw new NotFoundException($"Vehicle {id} not found.");
        CentreAccessGuard.AssertCanAccess(user.CentreAccess, vehicle.CentreId);

        if (cmd.TankerNumber is not null) vehicle.TankerNumber = cmd.TankerNumber;
        if (cmd.DriverName is not null) vehicle.DriverName = cmd.DriverName;
        if (cmd.DriverMobile is not null) vehicle.DriverMobile = cmd.DriverMobile;
        if (cmd.CapacityKg is not null) vehicle.CapacityKg = cmd.CapacityKg;
        if (cmd.Status is not null) vehicle.Status = cmd.Status.Value;
        vehicle.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return vehicle;
    }
}
