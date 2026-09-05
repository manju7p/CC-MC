using Microsoft.AspNetCore.Authorization;

namespace CCMC.Cloud.Api.Auth;

/// <summary>
/// Backend-authoritative permission check (BRD v2 section 14 / this
/// session's RBAC requirement) - every protected endpoint must declare
/// [RequirePermission(code)] explicitly; there is no generic policy engine
/// and no endpoint silently "allowed" by omission (PermissionPolicyProvider
/// only recognizes policy names of the form "Permission:<code>" - an
/// endpoint with a typo'd or missing attribute simply gets no authorization
/// policy applied by this mechanism, which is why every controller action
/// below is deliberately explicit).
/// </summary>
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "Permission:";

    public RequirePermissionAttribute(string permissionCode) => Policy = PolicyPrefix + permissionCode;
}

public sealed class PermissionRequirement(string permissionCode) : IAuthorizationRequirement
{
    public string PermissionCode { get; } = permissionCode;
}

public sealed class PermissionAuthorizationHandler(ICurrentUserAccessor currentUserAccessor)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        try
        {
            var user = await currentUserAccessor.GetRequiredAsync(CancellationToken.None);
            if (user.PermissionCodes.Contains(requirement.PermissionCode))
            {
                context.Succeed(requirement);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the requirement unsatisfied -> the authorization
            // middleware produces 403, not a 500 from an unhandled exception.
        }
    }
}

/// <summary>
/// Dynamically builds an authorization policy for any policy name of the
/// form "Permission:<code>" - the standard ASP.NET Core pattern for a
/// permission set that isn't fixed at startup, avoiding having to
/// pre-register one AddPolicy(...) call per permission code by hand.
/// </summary>
public sealed class PermissionPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var code = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(code))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
