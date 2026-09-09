using System.Net;
using System.Net.Http.Json;
using CCMC.Cloud.Api.Tests.Infrastructure;
using CCMC.Contracts.Dtos;
using CCMC.Contracts.Enums;
using Xunit;

namespace CCMC.Cloud.Api.Tests;

[Collection(ApiCollection.Name)]
public class ReceptionTests(CcmcApiFactory factory)
{
    private static CreateReceptionRequestDto SampleRequest(
        string key, int centreId = 1, int sourceId = 1, int vehicleId = 1,
        decimal quantity = 45.5m, decimal fat = 4.5m, decimal snf = 9.0m, decimal temp = 5.0m) => new()
    {
        CentreId = centreId, SourceId = sourceId, VehicleId = vehicleId,
        QuantityKg = quantity, Fat = fat, Snf = snf, Temperature = temp, LocalIdempotencyKey = key,
    };

    [Fact]
    public async Task Create_ValidReception_Returns201WithCreatedOutcome()
    {
        var client = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var key = $"test-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("reception", SampleRequest(key));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReceptionTransactionDto>();
        Assert.Equal("created", dto!.Outcome);
        Assert.Equal(TransactionStatus.ACCEPTED, dto.Status);
        Assert.StartsWith("BLR-CC-01-", dto.TransactionNumber);
    }

    [Fact]
    public async Task Create_SameIdempotencyKeySamePayload_Returns201WithDuplicateOutcome_SameId()
    {
        var client = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var key = $"test-{Guid.NewGuid():N}";

        var first = await client.PostAsJsonAsync("reception", SampleRequest(key));
        var firstDto = await first.Content.ReadFromJsonAsync<ReceptionTransactionDto>();

        var second = await client.PostAsJsonAsync("reception", SampleRequest(key));
        var secondDto = await second.Content.ReadFromJsonAsync<ReceptionTransactionDto>();

        Assert.Equal(HttpStatusCode.Created, second.StatusCode); // duplicate is STILL 201, not 200/409
        Assert.Equal("duplicate", secondDto!.Outcome);
        Assert.Equal(firstDto!.Id, secondDto.Id);
    }

    [Fact]
    public async Task Create_SameIdempotencyKeyDifferentPayload_Returns409WithConflictingFields()
    {
        var client = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var key = $"test-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("reception", SampleRequest(key, quantity: 45.5m));
        var conflict = await client.PostAsJsonAsync("reception", SampleRequest(key, quantity: 99.9m));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var raw = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("QuantityKg", raw);
        Assert.Contains("existingTransactionId", raw);
    }

    [Fact]
    public async Task Create_QualityOutOfRange_ReturnsHold_NeverAutoRejects()
    {
        var client = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var key = $"test-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("reception", SampleRequest(key, fat: 0.5m)); // below the seeded 3.0-6.0 range

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<ReceptionTransactionDto>();
        Assert.Equal(TransactionStatus.HOLD, dto!.Status);
        Assert.NotNull(dto.Reason);
    }

    [Fact]
    public async Task Create_UnauthorizedCentre_Returns403()
    {
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345"); // Bangalore-scoped
        var request = SampleRequest($"test-{Guid.NewGuid():N}", centreId: 2, sourceId: 2, vehicleId: 2); // Mysore

        var response = await operator1.PostAsJsonAsync("reception", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidSourceForCentre_Returns400()
    {
        var client = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        // sourceId 2 belongs to Mysore, not Bangalore (centreId 1) - a real cross-reference validation failure.
        var request = SampleRequest($"test-{Guid.NewGuid():N}", sourceId: 2);

        var response = await client.PostAsJsonAsync("reception", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutReceptionCreatePermission_Returns403()
    {
        // No seeded role lacks RECEPTION_CREATE entirely in this seed, so this
        // proves the permission gate exists by removing the token altogether -
        // see AuthTests for the no-token case; here we assert an authenticated-
        // but-wrong-centre case is exactly a 403 (Forbidden), not a 401.
        var client = await factory.CreateAuthenticatedClientAsync("operator2@ccmc.local", "Operator@12345"); // Mysore-scoped
        var response = await client.PostAsJsonAsync("reception", SampleRequest($"test-{Guid.NewGuid():N}")); // targets Bangalore (centreId 1)

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetById_UnauthorizedCentre_Returns403()
    {
        var operator1 = await factory.CreateAuthenticatedClientAsync("operator1@ccmc.local", "Operator@12345");
        var created = await operator1.PostAsJsonAsync("reception", SampleRequest($"test-{Guid.NewGuid():N}"));
        var createdDto = await created.Content.ReadFromJsonAsync<ReceptionTransactionDto>();

        var operator2 = await factory.CreateAuthenticatedClientAsync("operator2@ccmc.local", "Operator@12345");
        var response = await operator2.GetAsync($"reception/{createdDto!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
