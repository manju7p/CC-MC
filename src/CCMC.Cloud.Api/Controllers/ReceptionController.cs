using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.Reception;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>
/// Every route/DTO shape here is verified against the Windows client's own
/// code (CCMC.Contracts.Dtos.CreateReceptionRequestDto/ReceptionTransactionDto/
/// OverrideReceptionRequestDto, CCMC.Infrastructure.Sync.HttpCloudApiClient,
/// HttpResponseClassifier) - nothing invented. POST /reception returns HTTP
/// 201 for BOTH "created" and "duplicate" outcomes (varying only the body's
/// `outcome` field) - this is REQUIRED, not incidental: the client's own
/// HttpResponseClassifier explicitly only recognizes 201 as success and
/// reads `outcome` from the body to distinguish the two, exactly the trap
/// its own regression test (Sync/HttpResponseClassifierTests) guards against.
/// </summary>
[ApiController]
[Route("reception")]
[Authorize]
public sealed class ReceptionController(ReceptionService receptionService, ICurrentUserAccessor currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionCodes.ReceptionView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var transactions = await receptionService.ListAsync(user, cancellationToken);
        return Ok(transactions.Select(t => t.ToDto()));
    }

    [HttpGet("{id:int}")]
    [RequirePermission(PermissionCodes.ReceptionView)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var transaction = await receptionService.GetByIdAsync(user, id, cancellationToken);
        return Ok(transaction.ToDto());
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.ReceptionCreate)]
    [ProducesResponseType(typeof(ReceptionTransactionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateReceptionRequestDto request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var result = await receptionService.CreateAsync(
            user,
            new CreateReceptionCommand(
                request.CentreId, request.SourceId, request.VehicleId,
                request.QuantityKg, request.Fat, request.Snf, request.Temperature,
                request.LocalIdempotencyKey),
            cancellationToken);

        var outcome = result.Outcome == CreateReceptionOutcome.Created ? "created" : "duplicate";
        // Deliberately 201 for both - see this controller's doc comment.
        return StatusCode(StatusCodes.Status201Created, result.Transaction.ToDto(outcome));
    }

    [HttpPost("{id:int}/override")]
    [RequirePermission(PermissionCodes.ReceptionOverride)]
    public async Task<IActionResult> Override(int id, [FromBody] OverrideReceptionRequestDto request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var transaction = await receptionService.OverrideAsync(
            user, id, new OverrideReceptionCommand(request.NewStatus.ToDomain(), request.Reason), cancellationToken);
        return Ok(transaction.ToDto());
    }
}
