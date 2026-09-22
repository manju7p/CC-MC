using System.Net;
using CCMC.Application.Abstractions;
using CCMC.Contracts.Dtos;
using CCMC.Infrastructure.Sync;
using Xunit;

namespace CCMC.Tests.Sync;

/// <summary>
/// Exercises the real HttpCloudApiClient deserialization path against
/// representative HTTP responses - not the serializer in isolation. JSON
/// fixtures below use STRING-encoded decimals (quantityKg: "45.50" etc.)
/// deliberately, matching the actual TypeORM entity shape verified during
/// Phase 0 (see context.md) - this is the exact real-world shape that,
/// before the FlexibleDecimalJsonConverter fix, threw an uncaught
/// JsonException and left outbox rows stuck in PROCESSING forever.
/// </summary>
public class HttpCloudApiClientTests
{
    private static HttpCloudApiClient CreateClient(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://fake.ccmc.test/"), Timeout = TimeSpan.FromSeconds(5) });

    private static readonly CreateReceptionRequestDto SampleRequest = new()
    {
        CentreId = 1,
        SourceId = 2,
        VehicleId = 3,
        QuantityKg = 45.50m,
        Fat = 4.53m,
        Snf = 8.72m,
        Temperature = 4.20m,
        LocalIdempotencyKey = "ccmc-desktop-test-key",
    };

    // --- Decimal-as-string deserialization (Blocker 1) --------------------

    [Fact]
    public async Task CreateReceptionAsync_StringEncodedDecimals_DeserializesCorrectly()
    {
        var json = """
            {
              "id": 501, "transactionNumber": "BLR-CC-01-501", "centreId": 1, "sourceId": 2, "vehicleId": 3,
              "operatorUserId": 9, "quantityKg": "45.50", "fat": "4.53", "snf": "8.72", "temperature": "4.20",
              "status": "ACCEPTED", "readingSource": "MANUAL", "reason": null,
              "receivedAt": "2026-09-05T10:00:00.000Z", "outcome": "created"
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, json));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Created, result.Outcome);
        Assert.NotNull(result.Transaction);
        Assert.Equal(45.50m, result.Transaction!.QuantityKg);
        Assert.Equal(4.53m, result.Transaction.Fat);
        Assert.Equal(8.72m, result.Transaction.Snf);
        Assert.Equal(4.20m, result.Transaction.Temperature);
    }

    [Fact]
    public async Task CreateReceptionAsync_NumericDecimals_AlsoDeserializesCorrectly()
    {
        // If the cloud ever changes to emit real JSON numbers, the converter must still accept them.
        var json = """
            {
              "id": 502, "transactionNumber": "BLR-CC-01-502", "centreId": 1, "sourceId": 2, "vehicleId": 3,
              "operatorUserId": 9, "quantityKg": 45.50, "fat": 4.53, "snf": 8.72, "temperature": 4.20,
              "status": "ACCEPTED", "readingSource": "MANUAL", "reason": null,
              "receivedAt": "2026-09-05T10:00:00.000Z", "outcome": "created"
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, json));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Created, result.Outcome);
        Assert.Equal(45.50m, result.Transaction!.QuantityKg);
    }

    [Fact]
    public async Task CreateReceptionAsync_DuplicateOutcome_ClassifiedAsDuplicate_NotCreated()
    {
        var json = """
            {
              "id": 501, "transactionNumber": "BLR-CC-01-501", "centreId": 1, "sourceId": 2, "vehicleId": 3,
              "operatorUserId": 9, "quantityKg": "45.50", "fat": "4.53", "snf": "8.72", "temperature": "4.20",
              "status": "ACCEPTED", "readingSource": "MANUAL", "reason": null,
              "receivedAt": "2026-09-05T10:00:00.000Z", "outcome": "duplicate"
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, json));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        // Regression: both created and duplicate are HTTP 201 - only the body's
        // `outcome` field distinguishes them (context.md).
        Assert.Equal(CloudReceptionOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task GetVehiclesAsync_StringCapacityKg_Deserializes()
    {
        var json = """
            [
              {
                "id": 1, "vehicleNumber": "KA01AB1234", "tankerNumber": "TNK-1234", "driverName": "Ramesh",
                "driverMobile": "9900000000", "capacityKg": "5000.00", "status": "ACTIVE", "centreId": 1
              },
              {
                "id": 2, "vehicleNumber": "KA09CD5678", "tankerNumber": null, "driverName": null,
                "driverMobile": null, "capacityKg": null, "status": "ACTIVE", "centreId": 1
              }
            ]
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, json));
        var client = CreateClient(handler);

        var vehicles = await client.GetVehiclesAsync("token", CancellationToken.None);

        Assert.Equal(2, vehicles.Count);
        Assert.Equal(5000.00m, vehicles[0].CapacityKg);
        Assert.Null(vehicles[1].CapacityKg);
    }

    [Fact]
    public async Task GetQualityRulesAsync_StringMinMaxValues_Deserializes()
    {
        var json = """
            [
              { "id": 1, "parameter": "FAT", "minValue": "3.00", "maxValue": "6.00", "centreId": null },
              { "id": 2, "parameter": "SNF", "minValue": "8.00", "maxValue": "10.00", "centreId": null }
            ]
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, json));
        var client = CreateClient(handler);

        var rules = await client.GetQualityRulesAsync("token", CancellationToken.None);

        Assert.Equal(2, rules.Count);
        Assert.Equal(3.00m, rules[0].MinValue);
        Assert.Equal(6.00m, rules[0].MaxValue);
    }

    [Fact]
    public async Task CreateReceptionAsync_MalformedDecimalValue_ReturnsControlledRetryable_DoesNotThrow()
    {
        var json = """
            {
              "id": 501, "transactionNumber": "BLR-CC-01-501", "centreId": 1, "sourceId": 2, "vehicleId": 3,
              "operatorUserId": 9, "quantityKg": "not-a-number", "fat": "4.53", "snf": "8.72", "temperature": "4.20",
              "status": "ACCEPTED", "readingSource": "MANUAL", "reason": null,
              "receivedAt": "2026-09-05T10:00:00.000Z", "outcome": "created"
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, json));
        var client = CreateClient(handler);

        // Must not throw - a malformed body is a controlled Retryable result,
        // not an uncaught exception that would leave an outbox row stuck.
        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Retryable, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }

    // --- HTTP status classification ---------------------------------------

    [Fact]
    public async Task CreateReceptionAsync_401_ReturnsAuthRetryable()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.AuthRetryable, result.Outcome);
    }

    [Fact]
    public async Task CreateReceptionAsync_403_ReturnsTerminal()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Forbidden, "{}"));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Terminal, result.Outcome);
    }

    [Fact]
    public async Task CreateReceptionAsync_404_ReturnsTerminal()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.NotFound, "{}"));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Terminal, result.Outcome);
    }

    [Fact]
    public async Task CreateReceptionAsync_409_ReturnsConflict_WithConflictingFields()
    {
        var json = """
            {
              "message": "localIdempotencyKey already used for a transaction with different data",
              "conflictingFields": ["quantityKg", "fat"],
              "existingTransactionId": 42
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Conflict, json));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Conflict, result.Outcome);
        Assert.NotNull(result.ConflictingFields);
        Assert.Contains("quantityKg", result.ConflictingFields!);
        Assert.Contains("fat", result.ConflictingFields!);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task CreateReceptionAsync_5xx_ReturnsRetryable(int statusCode)
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse((HttpStatusCode)statusCode, "{}"));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Retryable, result.Outcome);
    }

    // --- Network-level failures ---------------------------------------------

    [Fact]
    public async Task CreateReceptionAsync_NetworkFailure_ReturnsRetryable_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler(throwing: _ => new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Retryable, result.Outcome);
    }

    [Fact]
    public async Task CreateReceptionAsync_Timeout_ReturnsRetryable_DoesNotThrow()
    {
        // A handler that outlives the HttpClient's own Timeout - HttpClient itself
        // cancels the request and raises TaskCanceledException, exactly like a
        // real network stall would; no real socket or live server is involved.
        var handler = new FakeHttpMessageHandler(respond: request =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, "{}");
        });
        var client = new HttpCloudApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://fake.ccmc.test/"),
            Timeout = TimeSpan.FromMilliseconds(100),
        });

        var result = await client.CreateReceptionAsync("token", SampleRequest, CancellationToken.None);

        Assert.Equal(CloudReceptionOutcome.Retryable, result.Outcome);
    }

    // --- Override -------------------------------------------------------------

    private static readonly OverrideReceptionRequestDto SampleOverrideRequest = new()
    {
        NewStatus = CCMC.Contracts.Enums.TransactionStatus.ACCEPTED,
        Reason = "Manager review",
    };

    [Fact]
    public async Task OverrideReceptionAsync_Success_ReturnsSuccessOutcome()
    {
        var json = """
            {
              "id": 501, "transactionNumber": "BLR-CC-01-501", "centreId": 1, "sourceId": 2, "vehicleId": 3,
              "operatorUserId": 9, "quantityKg": "45.50", "fat": "4.53", "snf": "8.72", "temperature": "4.20",
              "status": "ACCEPTED", "readingSource": "MANUAL", "reason": "Manager review",
              "receivedAt": "2026-09-05T10:00:00.000Z"
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, json));
        var client = CreateClient(handler);

        var result = await client.OverrideReceptionAsync("token", 501, SampleOverrideRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Success, result.Outcome);
        Assert.Equal(CCMC.Contracts.Enums.TransactionStatus.ACCEPTED, result.Transaction!.Status);
    }

    [Fact]
    public async Task OverrideReceptionAsync_400NotOnHold_ReturnsTerminal_NotRetryable()
    {
        // The documented cloud contract gap: no idempotency key for this
        // endpoint, so a "not currently HOLD" 400 must be treated as terminal
        // (for manual review), never silently retried or assumed successful.
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest, """{"message":"Only a transaction currently on HOLD can be overridden"}"""));
        var client = CreateClient(handler);

        var result = await client.OverrideReceptionAsync("token", 501, SampleOverrideRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Terminal, result.Outcome);
    }

    [Fact]
    public async Task OverrideReceptionAsync_401_ReturnsAuthRetryable()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        var client = CreateClient(handler);

        var result = await client.OverrideReceptionAsync("token", 501, SampleOverrideRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.AuthRetryable, result.Outcome);
    }

    [Fact]
    public async Task OverrideReceptionAsync_500_ReturnsRetryable()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.InternalServerError, "{}"));
        var client = CreateClient(handler);

        var result = await client.OverrideReceptionAsync("token", 501, SampleOverrideRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Retryable, result.Outcome);
    }

    [Fact]
    public async Task OverrideReceptionAsync_NetworkFailure_ReturnsRetryable_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler(throwing: _ => new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var result = await client.OverrideReceptionAsync("token", 501, SampleOverrideRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Retryable, result.Outcome);
    }

    // --- Login --------------------------------------------------------------

    [Fact]
    public async Task LoginAsync_Success_ReturnsSessionDetails()
    {
        var json = """
            {
              "accessToken": "jwt-token-value",
              "user": {
                "id": 1, "email": "operator1@ccmc.local", "fullName": "Bangalore Operator",
                "roles": ["Operator"], "permissions": ["RECEPTION_CREATE"],
                "centreAccess": { "allCentres": false, "centreIds": [1] }
              }
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Created, json));
        var client = CreateClient(handler);

        var result = await client.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("jwt-token-value", result.Response!.AccessToken);
        Assert.Equal(1, result.Response.User.Id);
    }

    [Fact]
    public async Task LoginAsync_401_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, """{"message":"Invalid credentials"}"""));
        var client = CreateClient(handler);

        var result = await client.LoginAsync("wrong@ccmc.local", "wrong-password", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Invalid credentials", result.ErrorMessage);
    }

    [Fact]
    public async Task LoginAsync_RealCloudApiResponseShape_DeserializesCorrectly()
    {
        // Byte-for-byte the actual response captured from the running
        // CCMC.Cloud.Api (GET/POST auth/login), HTTP 200 - not a guessed
        // shape. Added during the login-bug investigation to prove the
        // client can genuinely parse what the real cloud returns (the bug
        // itself turned out to be an http/https scheme mismatch in
        // appsettings.json, not a deserialization problem - this test rules
        // deserialization back out for any future regression).
        var json = """
            {
              "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIzIiwiZW1haWwiOiJvcGVyYXRvcjFAY2NtYy5sb2NhbCJ9.signature",
              "user": {
                "id": 3,
                "email": "operator1@ccmc.local",
                "fullName": "Bangalore Operator",
                "roles": ["Operator"],
                "permissions": ["QUALITY_RULE_VIEW", "RECEPTION_VIEW", "RECEPTION_CREATE", "SOURCE_VIEW", "DASHBOARD_VIEW", "VEHICLE_VIEW"],
                "centreAccess": { "allCentres": false, "centreIds": [1] }
              }
            }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, json));
        var client = CreateClient(handler);

        var result = await client.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.IsNetworkFailure);
        Assert.NotNull(result.Response);
        Assert.Equal(3, result.Response!.User.Id);
        Assert.Equal("operator1@ccmc.local", result.Response.User.Email);
        Assert.False(result.Response.User.CentreAccess.AllCentres);
        Assert.Contains(1, result.Response.User.CentreAccess.CentreIds);
    }

    [Fact]
    public async Task LoginAsync_MalformedSuccessBody_ReturnsFailure_NotFlaggedAsNetworkFailure()
    {
        // The cloud reached us and said 200, but the body doesn't match
        // LoginResponseDto - this must be classified as a response-contract
        // problem, never as "cannot reach the cloud" (IsNetworkFailure must
        // stay false so AuthenticationService never mistakes it for a
        // connectivity failure and silently falls back to an offline cache).
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, "{ not valid json"));
        var client = CreateClient(handler);

        var result = await client.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsNetworkFailure);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task LoginAsync_ConnectionRefused_IsFlaggedAsNetworkFailure()
    {
        var handler = new FakeHttpMessageHandler(throwing: _ => new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var result = await client.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.IsNetworkFailure);
    }

    // --- Rate formula settings update (Manager-only Rate Configuration screen) ------------

    private static readonly UpsertRateFormulaSettingsRequestDto SampleRateFormulaRequest = new()
    {
        CentreId = 1,
        RateType = CCMC.Contracts.Enums.RateFormulaType.FAT_VS_SNF,
        Value1 = 10m,
        Value2 = 8m,
        TsRate = null,
    };

    [Fact]
    public async Task UpdateRateFormulaSettingsAsync_200_ReturnsSuccessWithSettings()
    {
        var json = """
            { "id": 7, "rateType": "FAT_VS_SNF", "value1": "10.00", "value2": "8.00", "tsRate": null, "centreId": 1 }
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, json));
        var client = CreateClient(handler);

        var result = await client.UpdateRateFormulaSettingsAsync("token", SampleRateFormulaRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Success, result.Outcome);
        Assert.NotNull(result.Settings);
        Assert.Equal(10m, result.Settings!.Value1);
        Assert.Equal(8m, result.Settings.Value2);
        Assert.Equal(1, result.Settings.CentreId);
    }

    /// <summary>A Manager attempting to configure a centre outside their access (or an Operator lacking RATE_FORMULA_CONFIGURE) gets 403 - classified Terminal, same as every other master-data mutation's 403.</summary>
    [Fact]
    public async Task UpdateRateFormulaSettingsAsync_403_ReturnsTerminal()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Forbidden, "{}"));
        var client = CreateClient(handler);

        var result = await client.UpdateRateFormulaSettingsAsync("token", SampleRateFormulaRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Terminal, result.Outcome);
        Assert.Null(result.Settings);
    }

    [Fact]
    public async Task UpdateRateFormulaSettingsAsync_401_ReturnsAuthRetryable()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, "{}"));
        var client = CreateClient(handler);

        var result = await client.UpdateRateFormulaSettingsAsync("token", SampleRateFormulaRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.AuthRetryable, result.Outcome);
    }

    [Fact]
    public async Task UpdateRateFormulaSettingsAsync_NetworkFailure_ReturnsRetryable_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler(throwing: _ => new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var result = await client.UpdateRateFormulaSettingsAsync("token", SampleRateFormulaRequest, CancellationToken.None);

        Assert.Equal(CloudMutationOutcome.Retryable, result.Outcome);
    }
}
