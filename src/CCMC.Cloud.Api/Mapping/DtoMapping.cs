using CCMC.Cloud.Application.Auth;
using CCMC.Cloud.Application.Dashboard;
using CCMC.Cloud.Domain.Entities;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using ContractsEnums = CCMC.Contracts.Enums;
using DomainEnums = CCMC.Cloud.Domain.Enums;

namespace CCMC.Cloud.Api.Mapping;

/// <summary>
/// The only place Domain enums/entities are translated to/from the wire
/// DTOs the Windows client actually expects (CCMC.Contracts, reused
/// directly - see CLAUDE.md "API Contract" for why this project references
/// the client's own Contracts library instead of redefining the same JSON
/// shapes independently).
/// </summary>
public static class DtoMapping
{
    public static ContractsEnums.RecordStatus ToContract(this DomainEnums.RecordStatus status) => status switch
    {
        DomainEnums.RecordStatus.Active => ContractsEnums.RecordStatus.ACTIVE,
        DomainEnums.RecordStatus.Inactive => ContractsEnums.RecordStatus.INACTIVE,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ContractsEnums.TransactionStatus ToContract(this DomainEnums.TransactionStatus status) => status switch
    {
        DomainEnums.TransactionStatus.Accepted => ContractsEnums.TransactionStatus.ACCEPTED,
        DomainEnums.TransactionStatus.Rejected => ContractsEnums.TransactionStatus.REJECTED,
        DomainEnums.TransactionStatus.Hold => ContractsEnums.TransactionStatus.HOLD,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static DomainEnums.TransactionStatus ToDomain(this ContractsEnums.TransactionStatus status) => status switch
    {
        ContractsEnums.TransactionStatus.ACCEPTED => DomainEnums.TransactionStatus.Accepted,
        ContractsEnums.TransactionStatus.REJECTED => DomainEnums.TransactionStatus.Rejected,
        ContractsEnums.TransactionStatus.HOLD => DomainEnums.TransactionStatus.Hold,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ContractsEnums.ReadingSource ToContract(this DomainEnums.ReadingSource source) => source switch
    {
        DomainEnums.ReadingSource.Manual => ContractsEnums.ReadingSource.MANUAL,
        DomainEnums.ReadingSource.Device => ContractsEnums.ReadingSource.DEVICE,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static ContractsEnums.QualityParameter ToContract(this DomainEnums.QualityParameter parameter) => parameter switch
    {
        DomainEnums.QualityParameter.Fat => ContractsEnums.QualityParameter.FAT,
        DomainEnums.QualityParameter.Snf => ContractsEnums.QualityParameter.SNF,
        DomainEnums.QualityParameter.Temperature => ContractsEnums.QualityParameter.TEMPERATURE,
        _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
    };

    public static AuthenticatedUserDto ToDto(this RequestUser user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        FullName = user.FullName,
        Roles = user.RoleNames.ToList(),
        Permissions = user.PermissionCodes.ToList(),
        CentreAccess = new CentreAccessSummaryDto { AllCentres = user.CentreAccess.AllCentres, CentreIds = user.CentreAccess.CentreIds.ToList() },
    };

    public static ChillingCentreDto ToDto(this ChillingCentre centre) => new()
    {
        Id = centre.Id, Code = centre.Code, Name = centre.Name, Status = centre.Status.ToContract(),
    };

    public static SourceDto ToDto(this Source source) => new()
    {
        Id = source.Id, Code = source.Code, Name = source.Name, Location = source.Location, Contact = source.Contact,
        MilkType = source.MilkType, Status = source.Status.ToContract(), CentreId = source.CentreId,
    };

    public static VehicleDto ToDto(this Vehicle vehicle) => new()
    {
        Id = vehicle.Id, VehicleNumber = vehicle.VehicleNumber, TankerNumber = vehicle.TankerNumber,
        DriverName = vehicle.DriverName, DriverMobile = vehicle.DriverMobile, CapacityKg = vehicle.CapacityKg,
        Status = vehicle.Status.ToContract(), CentreId = vehicle.CentreId,
    };

    public static QualityRuleDto ToDto(this QualityRule rule) => new()
    {
        Id = rule.Id, Parameter = rule.Parameter.ToContract(), MinValue = rule.MinValue, MaxValue = rule.MaxValue, CentreId = rule.CentreId,
    };

    public static ReceptionTransactionDto ToDto(this MilkReceptionTransaction t, string? outcome = null) => new()
    {
        Id = t.Id, TransactionNumber = t.TransactionNumber, CentreId = t.CentreId, SourceId = t.SourceId, VehicleId = t.VehicleId,
        OperatorUserId = t.OperatorUserId, QuantityKg = t.QuantityKg, Fat = t.Fat, Snf = t.Snf, Temperature = t.Temperature,
        Clr = t.Clr, Water = t.Water, Protein = t.Protein, RawAnalyserPayload = t.RawAnalyserPayload,
        Status = t.Status.ToContract(), ReadingSource = t.ReadingSource.ToContract(), Reason = t.Reason,
        ReceivedAt = t.ReceivedAt.ToString("O"), Outcome = outcome,
    };

    public static DashboardSummaryDto ToDto(this DashboardSummary summary) => new()
    {
        Date = summary.DateLabel, CentreIds = summary.CentreIds.ToList(), TotalTransactions = summary.TotalTransactions,
        Accepted = summary.Accepted, Rejected = summary.Rejected, Hold = summary.Hold,
    };

    public static AuditLogDto ToDto(this AuditLog log) => new()
    {
        Id = log.Id, UserId = log.UserId, CentreId = log.CentreId, Action = log.Action, ResourceType = log.ResourceType,
        ResourceId = log.ResourceId, Reason = log.Reason, CreatedAt = log.CreatedAt.ToString("O"),
    };
}
