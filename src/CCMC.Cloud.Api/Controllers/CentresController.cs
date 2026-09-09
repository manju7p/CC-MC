using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.MasterData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>
/// GET /centres - reference data (which centres this user can act within).
/// Authenticated only, no specific permission required - matches the
/// client's own documented expectation (context.md "Cloud Responsibilities" -
/// "no separate centre management capability in this slice").
/// </summary>
[ApiController]
[Route("centres")]
[Authorize]
public sealed class CentresController(CentreService centreService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var centres = await centreService.ListAsync(user, cancellationToken);
        return Ok(centres.Select(c => c.ToDto()));
    }
}
