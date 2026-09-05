using System.Text.Json.Serialization;

namespace CCMC.Contracts.Auth;

public sealed class LoginRequestDto
{
    [JsonPropertyName("email")] public required string Email { get; init; }
    [JsonPropertyName("password")] public required string Password { get; init; }
}

public sealed class CentreAccessSummaryDto
{
    [JsonPropertyName("allCentres")] public required bool AllCentres { get; init; }
    [JsonPropertyName("centreIds")] public required List<int> CentreIds { get; init; }
}

public sealed class AuthenticatedUserDto
{
    [JsonPropertyName("id")] public required int Id { get; init; }
    [JsonPropertyName("email")] public required string Email { get; init; }
    [JsonPropertyName("fullName")] public required string FullName { get; init; }
    [JsonPropertyName("roles")] public required List<string> Roles { get; init; }
    [JsonPropertyName("permissions")] public required List<string> Permissions { get; init; }
    [JsonPropertyName("centreAccess")] public required CentreAccessSummaryDto CentreAccess { get; init; }
}

public sealed class LoginResponseDto
{
    [JsonPropertyName("accessToken")] public required string AccessToken { get; init; }
    [JsonPropertyName("user")] public required AuthenticatedUserDto User { get; init; }
}
