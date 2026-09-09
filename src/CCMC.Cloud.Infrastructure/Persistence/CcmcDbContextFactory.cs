using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CCMC.Cloud.Infrastructure.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations add/database update` can run
/// without the full CCMC.Cloud.Api host - reads CCMC_DB_CONNECTION_STRING
/// from the environment (falling back to a clearly-local-only default) so no
/// real credential is ever hard-coded here. This is a development/tooling
/// convenience only - the running API always gets its connection string from
/// its own configuration (appsettings/environment), never from this class.
/// </summary>
public sealed class CcmcDbContextFactory : IDesignTimeDbContextFactory<CcmcDbContext>
{
    public CcmcDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CCMC_DB_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=ccmc_cloud_dev;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<CcmcDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new CcmcDbContext(optionsBuilder.Options);
    }
}
