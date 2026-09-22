namespace CCMC.Contracts.Auth;

/// <summary>
/// Mirrors the cloud API's PERMISSIONS constant object exactly (@cc-mc/shared-types,
/// verified during Phase 0 - see context.md). This is the same set of codes the
/// cloud's PermissionGuard enforces server-side; the Windows app's own use of
/// these is UX-only (hiding controls), never an enforcement point - the cloud
/// remains authoritative, per context.md "Application Responsibilities".
/// </summary>
public static class PermissionCodes
{
    public const string SourceView = "SOURCE_VIEW";
    public const string SourceCreate = "SOURCE_CREATE";
    public const string SourceEdit = "SOURCE_EDIT";

    public const string VehicleView = "VEHICLE_VIEW";
    public const string VehicleCreate = "VEHICLE_CREATE";
    public const string VehicleEdit = "VEHICLE_EDIT";

    public const string QualityRuleView = "QUALITY_RULE_VIEW";
    public const string QualityRuleConfigure = "QUALITY_RULE_CONFIGURE";

    public const string RateFormulaView = "RATE_FORMULA_VIEW";
    public const string RateFormulaConfigure = "RATE_FORMULA_CONFIGURE";

    public const string ReceptionView = "RECEPTION_VIEW";
    public const string ReceptionCreate = "RECEPTION_CREATE";
    public const string ReceptionOverride = "RECEPTION_OVERRIDE";

    public const string DashboardView = "DASHBOARD_VIEW";
    public const string AuditView = "AUDIT_VIEW";
}
