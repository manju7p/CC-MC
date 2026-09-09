using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.MasterData;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>GET /quality-rules is the route the Windows client actually calls (HttpCloudApiClient.GetQualityRulesAsync, cached locally for fully-offline validation).</summary>
[ApiController]
[Route("quality-rules")]
[Authorize]
public sealed class QualityRulesController(QualityRuleService qualityRuleService) : ControllerBase
{
    public sealed record UpdateQualityRuleRequest(decimal MinValue, decimal MaxValue);

    [HttpGet]
    [RequirePermission(PermissionCodes.QualityRuleView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var rules = await qualityRuleService.ListAsync(cancellationToken);
        return Ok(rules.Select(r => r.ToDto()));
    }

    [HttpPatch("{id:int}")]
    [RequirePermission(PermissionCodes.QualityRuleConfigure)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateQualityRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await qualityRuleService.UpdateAsync(id, new UpdateQualityRuleCommand(request.MinValue, request.MaxValue), cancellationToken);
        return Ok(rule.ToDto());
    }
}
