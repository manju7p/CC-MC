using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.Dashboard;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>GET /dashboard/summary[?centreId=] - matches HttpCloudApiClient.GetDashboardSummaryAsync exactly.</summary>
[ApiController]
[Route("dashboard")]
[Authorize]
public sealed class DashboardController(DashboardService dashboardService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet("summary")]
    [RequirePermission(PermissionCodes.DashboardView)]
    public async Task<IActionResult> Summary([FromQuery] int? centreId, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var summary = await dashboardService.GetSummaryAsync(user, centreId, cancellationToken);
        return Ok(summary.ToDto());
    }
}
