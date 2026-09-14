using Microsoft.Extensions.Configuration;

namespace CCMC.Cloud.Infrastructure.Seed;

/// <summary>
/// Bound from the "Bootstrap" configuration section (env vars: Bootstrap__*),
/// never from a tracked appsettings file - these are real production
/// identities/credentials the operator supplies once, the same way
/// Jwt__Secret/ConnectionStrings__CcmcDb already work in this repo. Absent
/// entirely (the default), Program.cs skips bootstrap altogether - this is
/// not a mechanism that runs unless explicitly asked to.
/// </summary>
public sealed record ProductionBootstrapOptions
{
    public required string AdminEmail { get; init; }
    public required string AdminFullName { get; init; }
    public required string AdminPassword { get; init; }

    public string? CentreCode { get; init; }
    public string? CentreName { get; init; }

    public string? ManagerEmail { get; init; }
    public string? ManagerFullName { get; init; }
    public string? ManagerPassword { get; init; }

    public string? OperatorEmail { get; init; }
    public string? OperatorFullName { get; init; }
    public string? OperatorPassword { get; init; }

    /// <summary>
    /// Null if Bootstrap:AdminEmail is unset (the normal case) - callers
    /// should treat null as "bootstrap not requested", not an error.
    /// Throws InvalidOperationException on a real configuration mistake
    /// (e.g. a Manager email with no centre), matching this repo's existing
    /// fail-fast pattern for JwtOptions.
    /// </summary>
    public static ProductionBootstrapOptions? FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Bootstrap");
        var adminEmail = section["AdminEmail"];
        if (string.IsNullOrWhiteSpace(adminEmail)) return null;

        var options = new ProductionBootstrapOptions
        {
            AdminEmail = adminEmail,
            AdminFullName = section["AdminFullName"] ?? "System Administrator",
            AdminPassword = section["AdminPassword"]
                ?? throw new InvalidOperationException("Bootstrap:AdminEmail is set but Bootstrap:AdminPassword is missing."),
            CentreCode = section["CentreCode"],
            CentreName = section["CentreName"],
            ManagerEmail = section["ManagerEmail"],
            ManagerFullName = section["ManagerFullName"],
            ManagerPassword = section["ManagerPassword"],
            OperatorEmail = section["OperatorEmail"],
            OperatorFullName = section["OperatorFullName"],
            OperatorPassword = section["OperatorPassword"],
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        ValidatePassword("AdminPassword", AdminPassword);

        var hasCentre = !string.IsNullOrWhiteSpace(CentreCode) && !string.IsNullOrWhiteSpace(CentreName);
        if (!string.IsNullOrWhiteSpace(CentreCode) != !string.IsNullOrWhiteSpace(CentreName))
        {
            throw new InvalidOperationException("Bootstrap:CentreCode and Bootstrap:CentreName must both be set, or both left unset.");
        }

        if (!string.IsNullOrWhiteSpace(ManagerEmail))
        {
            if (!hasCentre)
            {
                throw new InvalidOperationException(
                    "Bootstrap:ManagerEmail requires Bootstrap:CentreCode and Bootstrap:CentreName - a Manager needs a valid centre scope, never an unscoped account.");
            }
            ValidatePassword("ManagerPassword", ManagerPassword);
        }

        if (!string.IsNullOrWhiteSpace(OperatorEmail))
        {
            if (!hasCentre)
            {
                throw new InvalidOperationException(
                    "Bootstrap:OperatorEmail requires Bootstrap:CentreCode and Bootstrap:CentreName - an Operator needs a valid centre scope, never an unscoped account.");
            }
            ValidatePassword("OperatorPassword", OperatorPassword);
        }
    }

    private static void ValidatePassword(string fieldName, string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new InvalidOperationException($"Bootstrap:{fieldName} must be set and at least 12 characters - never a weak or missing bootstrap password.");
        }
    }
}
