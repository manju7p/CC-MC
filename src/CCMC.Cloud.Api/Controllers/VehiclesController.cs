using CCMC.Cloud.Api.Auth;
using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.MasterData;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>GET /vehicles is the route the Windows client actually calls (HttpCloudApiClient.GetVehiclesAsync). See SourcesController's doc comment for POST/PATCH's rationale.</summary>
[ApiController]
[Route("vehicles")]
[Authorize]
public sealed class VehiclesController(VehicleService vehicleService, ICurrentUserAccessor currentUser) : ControllerBase
{
    public sealed record CreateVehicleRequest(string VehicleNumber, string? TankerNumber, string? DriverName, string? DriverMobile, decimal? CapacityKg, int CentreId);
    public sealed record UpdateVehicleRequest(string? TankerNumber, string? DriverName, string? DriverMobile, decimal? CapacityKg);

    [HttpGet]
    [RequirePermission(PermissionCodes.VehicleView)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var vehicles = await vehicleService.ListAsync(user, cancellationToken);
        return Ok(vehicles.Select(v => v.ToDto()));
    }

    [HttpGet("{id:int}")]
    [RequirePermission(PermissionCodes.VehicleView)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var vehicle = await vehicleService.GetByIdAsync(user, id, cancellationToken);
        return Ok(vehicle.ToDto());
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.VehicleCreate)]
    public async Task<IActionResult> Create([FromBody] CreateVehicleRequest request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var vehicle = await vehicleService.CreateAsync(
            user, new CreateVehicleCommand(request.VehicleNumber, request.TankerNumber, request.DriverName, request.DriverMobile, request.CapacityKg, request.CentreId),
            cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = vehicle.Id }, vehicle.ToDto());
    }

    [HttpPatch("{id:int}")]
    [RequirePermission(PermissionCodes.VehicleEdit)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateVehicleRequest request, CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredAsync(cancellationToken);
        var vehicle = await vehicleService.UpdateAsync(
            user, id, new UpdateVehicleCommand(request.TankerNumber, request.DriverName, request.DriverMobile, request.CapacityKg, null), cancellationToken);
        return Ok(vehicle.ToDto());
    }
}
