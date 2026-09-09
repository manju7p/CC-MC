using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.MasterData;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>
/// GET /sources is the route the Windows client actually calls
/// (HttpCloudApiClient.GetSourcesAsync). POST/PATCH are additive - the
/// client has no create/edit UI yet, but the BRD requires CRUD support and
/// the QUALITY_RULE_CONFIGURE-style permission codes (SOURCE_CREATE/EDIT)
/// already exist in the shared Contracts, so the endpoints are provided for
/// when that UI is built.
/// </summary>
[ApiController]
[Route("sources")]
[Authorize]
public sealed class SourcesController(SourceService sourceService, ICurrentUserAccessor currentUser) : ControllerBase
{
    public sealed record CreateSourceRequest(string Code, string Name, string? Location, string? Contact, string? MilkType, int CentreId);
    public sealed record UpdateSourceRequest(string? Name, string? Location, string? Contact, string? MilkType);

    [HttpGet]
    [RequirePermission(PermissionCodes.SourceView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var sources = await sourceService.ListAsync(user, cancellationToken);
        return Ok(sources.Select(s => s.ToDto()));
    }

    [HttpGet("{id:int}")]
    [RequirePermission(PermissionCodes.SourceView)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var source = await sourceService.GetByIdAsync(user, id, cancellationToken);
        return Ok(source.ToDto());
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.SourceCreate)]
    public async Task<IActionResult> Create([FromBody] CreateSourceRequest request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var source = await sourceService.CreateAsync(
            user, new CreateSourceCommand(request.Code, request.Name, request.Location, request.Contact, request.MilkType, request.CentreId),
            cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = source.Id }, source.ToDto());
    }

    [HttpPatch("{id:int}")]
    [RequirePermission(PermissionCodes.SourceEdit)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSourceRequest request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var source = await sourceService.UpdateAsync(
            user, id, new UpdateSourceCommand(request.Name, request.Location, request.Contact, request.MilkType, null), cancellationToken);
        return Ok(source.ToDto());
    }
}
