using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using ContractsEnums = CCMC.Contracts.Enums;

namespace CCMC.Application.MasterData;

/// <summary>
/// Pulls reference data (centres/sources/vehicles/quality rules) from the
/// cloud into the local SQLite cache, so reception can run entirely offline
/// afterwards using whatever was last pulled. Call after login when online;
/// safe to skip when offline - existing local rows are simply left as-is.
/// </summary>
public sealed class MasterDataSyncService(
    ICloudApiClient cloudApiClient,
    IChillingCentreRepository centreRepository,
    ISourceRepository sourceRepository,
    IVehicleRepository vehicleRepository,
    IQualityRuleRepository qualityRuleRepository)
{
    public async Task PullAsync(string accessToken, CancellationToken cancellationToken)
    {
        var centres = await cloudApiClient.GetCentresAsync(accessToken, cancellationToken);
        await centreRepository.ReplaceAllAsync(centres.Select(MapCentre).ToList(), cancellationToken);

        var sources = await cloudApiClient.GetSourcesAsync(accessToken, cancellationToken);
        await sourceRepository.ReplaceAllAsync(sources.Select(MapSource).ToList(), cancellationToken);

        var vehicles = await cloudApiClient.GetVehiclesAsync(accessToken, cancellationToken);
        await vehicleRepository.ReplaceAllAsync(vehicles.Select(MapVehicle).ToList(), cancellationToken);

        var rules = await cloudApiClient.GetQualityRulesAsync(accessToken, cancellationToken);
        await qualityRuleRepository.ReplaceAllAsync(rules.Select(MapRule).ToList(), cancellationToken);
    }

    private static ChillingCentre MapCentre(Contracts.Dtos.ChillingCentreDto dto) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,
        Status = dto.Status == ContractsEnums.RecordStatus.ACTIVE ? RecordStatus.Active : RecordStatus.Inactive,
    };

    private static Source MapSource(Contracts.Dtos.SourceDto dto) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,
        Location = dto.Location,
        Contact = dto.Contact,
        MilkType = dto.MilkType,
        Status = dto.Status == ContractsEnums.RecordStatus.ACTIVE ? RecordStatus.Active : RecordStatus.Inactive,
        CentreId = dto.CentreId,
    };

    private static Vehicle MapVehicle(Contracts.Dtos.VehicleDto dto) => new()
    {
        Id = dto.Id,
        VehicleNumber = dto.VehicleNumber,
        TankerNumber = dto.TankerNumber,
        DriverName = dto.DriverName,
        DriverMobile = dto.DriverMobile,
        CapacityKg = dto.CapacityKg,
        Status = dto.Status == ContractsEnums.RecordStatus.ACTIVE ? RecordStatus.Active : RecordStatus.Inactive,
        CentreId = dto.CentreId,
    };

    private static QualityRule MapRule(Contracts.Dtos.QualityRuleDto dto) => new()
    {
        Id = dto.Id,
        Parameter = dto.Parameter switch
        {
            ContractsEnums.QualityParameter.FAT => QualityParameter.Fat,
            ContractsEnums.QualityParameter.SNF => QualityParameter.Snf,
            ContractsEnums.QualityParameter.TEMPERATURE => QualityParameter.Temperature,
            _ => throw new ArgumentOutOfRangeException(nameof(dto), $"Unknown quality parameter: {dto.Parameter}"),
        },
        MinValue = dto.MinValue,
        MaxValue = dto.MaxValue,
        CentreId = dto.CentreId,
    };
}
