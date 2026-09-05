using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.Audit;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

[ApiController]
[Route("audit-logs")]
[Authorize]
public sealed class AuditLogsController(AuditQueryService auditQueryService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionCodes.AuditView)]
    public async Task<IActionResult> List([FromQuery] string? resourceType, [FromQuery] int? centreId, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var logs = await auditQueryService.ListAsync(user, resourceType, centreId, cancellationToken);
        return Ok(logs.Select(l => l.ToDto()));
    }
}
