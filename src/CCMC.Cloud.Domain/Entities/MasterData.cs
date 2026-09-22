using CCMC.Cloud.Domain.Enums;

namespace CCMC.Cloud.Domain.Entities;

public sealed class ChillingCentre
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Source
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Location { get; set; }
    public string? Contact { get; set; }
    public string? MilkType { get; set; }
    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public int CentreId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ChillingCentre Centre { get; set; } = null!;
}

public sealed class Vehicle
{
    public int Id { get; set; }
    public required string VehicleNumber { get; set; }
    public string? TankerNumber { get; set; }
    public string? DriverName { get; set; }
    public string? DriverMobile { get; set; }

    /// <summary>NUMERIC(10,2) - up to 99,999,999.99 kg, comfortably above any real tanker capacity.</summary>
    public decimal? CapacityKg { get; set; }

    public RecordStatus Status { get; set; } = RecordStatus.Active;
    public int CentreId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ChillingCentre Centre { get; set; } = null!;
}

/// <summary>Null CentreId = global default rule, resolved centre-specific-over-global.</summary>
public sealed class QualityRule
{
    public int Id { get; set; }
    public QualityParameter Parameter { get; set; }

    /// <summary>NUMERIC(6,2) - covers realistic FAT/SNF percentage and temperature (°C) ranges with 2-decimal lab precision.</summary>
    public decimal MinValue { get; set; }
    public decimal MaxValue { get; set; }

    public int? CentreId { get; set; }
    public ChillingCentre? Centre { get; set; }
}

/// <summary>
/// BRD v5.0 section 25 (PREFS_RATE_*), cloud-owned configuration mirroring
/// QualityRule's centre-specific-over-global resolution. Value1/Value2/TsRate
/// stay nullable - the BRD explicitly allows an unconfigured formula (Rate/
/// Amount then computed as 0, never a fabricated default).
/// </summary>
public sealed class RateFormulaSettings
{
    public int Id { get; set; }
    public RateFormulaType RateType { get; set; }
    public decimal? Value1 { get; set; }
    public decimal? Value2 { get; set; }
    public decimal? TsRate { get; set; }
    public int? CentreId { get; set; }
    public ChillingCentre? Centre { get; set; }
}
