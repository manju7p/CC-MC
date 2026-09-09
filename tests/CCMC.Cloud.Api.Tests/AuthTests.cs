using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CCMC.Cloud.Api.Tests.Infrastructure;
using CCMC.Contracts.Auth;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(CcmcApiFactory factory)
{
    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenAndUser()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = "operator1@ccmc.local", Password = "Operator@12345" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body!.AccessToken));
        Assert.Equal("operator1@ccmc.local", body.User.Email);
        Assert.Contains("Operator", body.User.Roles);
        Assert.Contains("RECEPTION_CREATE", body.User.Permissions);
        Assert.False(body.User.CentreAccess.AllCentres);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = "operator1@ccmc.local", Password = "WrongPassword" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401_SameAsWrongPassword()
    {
        // Must not distinguish "unknown user" from "wrong password" via a different status/shape - email enumeration protection.
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = "nobody-such-user@ccmc.local", Password = "AnyPassword@123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_NoToken_Returns401()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_InvalidToken_Returns401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "this-is-not-a-valid-jwt");

        var response = await client.GetAsync("sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginResponse_NeverContainsPasswordHash()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("auth/login", new LoginRequestDto { Email = "operator1@ccmc.local", Password = "Operator@12345" });
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordHash", raw);
    }
}
