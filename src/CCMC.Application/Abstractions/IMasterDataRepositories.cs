using CCMC.Domain.Entities;
using CCMC.Domain.Enums;

namespace CCMC.Application.Abstractions;

public interface ISourceRepository
{
    Task<IReadOnlyList<Source>> ListByCentreAsync(int centreId, CancellationToken cancellationToken);
    Task<Source?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task ReplaceAllAsync(IReadOnlyList<Source> sources, CancellationToken cancellationToken);
}

public interface IVehicleRepository
{
    Task<IReadOnlyList<Vehicle>> ListByCentreAsync(int centreId, CancellationToken cancellationToken);
    Task<Vehicle?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task ReplaceAllAsync(IReadOnlyList<Vehicle> vehicles, CancellationToken cancellationToken);
}

public interface IChillingCentreRepository
{
    Task<IReadOnlyList<ChillingCentre>> ListAsync(CancellationToken cancellationToken);
    Task ReplaceAllAsync(IReadOnlyList<ChillingCentre> centres, CancellationToken cancellationToken);
}

public interface IQualityRuleRepository
{
    Task<IReadOnlyList<QualityRule>> ListAsync(CancellationToken cancellationToken);
    Task ReplaceAllAsync(IReadOnlyList<QualityRule> rules, CancellationToken cancellationToken);

    /// <summary>Resolves centre-specific-over-global for each requested parameter (mirrors the cloud's own resolution rule).</summary>
    Task<IReadOnlyDictionary<QualityParameter, QualityRule>> ResolveForCentreAsync(
        int centreId, IReadOnlyList<QualityParameter> parameters, CancellationToken cancellationToken);
}

public interface IRateFormulaSettingsRepository
{
    Task<IReadOnlyList<RateFormulaSettings>> ListAsync(CancellationToken cancellationToken);
    Task ReplaceAllAsync(IReadOnlyList<RateFormulaSettings> settings, CancellationToken cancellationToken);

    /// <summary>Resolves centre-specific-over-global (mirrors IQualityRuleRepository.ResolveForCentreAsync) - null if nothing is configured for this centre or globally.</summary>
    Task<RateFormulaSettings?> ResolveForCentreAsync(int centreId, CancellationToken cancellationToken);
}

public interface IAuditLogRepository
{
    Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(int take, CancellationToken cancellationToken);
}

public interface IDeviceConfigurationRepository
{
    Task<DeviceConfiguration?> GetAsync(DeviceKind kind, CancellationToken cancellationToken);
    Task UpsertAsync(DeviceConfiguration configuration, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceConfiguration>> ListAsync(CancellationToken cancellationToken);
}
