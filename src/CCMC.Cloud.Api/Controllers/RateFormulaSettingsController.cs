using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.MasterData;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>GET /rate-formula-settings is the route the Windows client actually calls (HttpCloudApiClient.GetRateFormulaSettingsAsync, cached locally for fully-offline rate calculation - BRD v5.0 section 25). PUT is the route the Windows client's Manager-only Rate Configuration screen calls (HttpCloudApiClient.UpdateRateFormulaSettingsAsync).</summary>
[ApiController]
[Route("rate-formula-settings")]
[Authorize]
public sealed class RateFormulaSettingsController(RateFormulaSettingsService rateFormulaSettingsService, ICurrentUserAccessor currentUser) : ControllerBase
{
    public sealed record UpsertRateFormulaSettingsRequest(int? CentreId, RateFormulaType RateType, decimal? Value1, decimal? Value2, decimal? TsRate);

    [HttpGet]
    [RequirePermission(PermissionCodes.RateFormulaView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var settings = await rateFormulaSettingsService.ListAsync(cancellationToken);
        return Ok(settings.Select(s => s.ToDto()));
    }

    [HttpPut]
    [RequirePermission(PermissionCodes.RateFormulaConfigure)]
    public async Task<IActionResult> Upsert([FromBody] UpsertRateFormulaSettingsRequest request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var settings = await rateFormulaSettingsService.UpsertAsync(
            user,
            new UpsertRateFormulaSettingsCommand(request.CentreId, request.RateType.ToDomain(), request.Value1, request.Value2, request.TsRate),
            cancellationToken);
        return Ok(settings.ToDto());
    }
}
