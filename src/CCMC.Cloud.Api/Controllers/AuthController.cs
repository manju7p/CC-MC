using CCMC.Cloud.Api.Mapping;
using CCMC.Cloud.Application.Auth;
using CCMC.Contracts.Auth;
using Microsoft.AspNetCore.Mvc;

namespace CCMC.Cloud.Api.Controllers;

/// <summary>
/// POST /auth/login - the exact route and request/response shape the
/// Windows client's HttpCloudApiClient.LoginAsync already expects
/// (CCMC.Contracts.Auth.LoginRequestDto/LoginResponseDto - verified against
/// that project directly, not guessed). Unprefixed route (no /api/v1),
/// matching the client's own documented routing (CLAUDE.md "API Contract").
/// </summary>
[ApiController]
[Route("auth")]
public sealed class AuthController(AuthenticationService authenticationService) : ControllerBase
{
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        var outcome = await authenticationService.LoginAsync(request.Email, request.Password, cancellationToken);
        if (!outcome.Success)
        {
            return Unauthorized(new { message = "Invalid credentials" });
        }

        return Ok(new LoginResponseDto { AccessToken = outcome.AccessToken!, User = outcome.User!.ToDto() });
    }
}
