using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CCMC.Application.Abstractions;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CCMC.Infrastructure.Sync;

/// <summary>
/// The Windows app's only path to the cloud. Every route called here is one
/// verified during Phase 0 inspection of the existing NestJS API (documented
/// in context.md "Cloud Responsibilities") - none invented. Routes are
/// unprefixed (no /api/v1) per the actual current API, not BRD v2 section
/// 17's illustrative examples.
///
/// Constructed with an HttpClient whose BaseAddress is already set to the
/// configured CloudApiOptions.BaseUrl (see CCMC.Desktop's composition root).
/// </summary>
public sealed class HttpCloudApiClient(HttpClient httpClient, ILogger<HttpCloudApiClient>? logger = null) : ICloudApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<HttpCloudApiClient> logger = logger ?? NullLogger<HttpCloudApiClient>.Instance;

    public async Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        // Login-path diagnostics only (see the login-bug investigation this
        // logging was added for): the target URL and outcome category are
        // logged so a future "cannot reach the cloud" report can be traced to
        // its real cause (e.g. an HTTP/HTTPS scheme mismatch) instead of
        // staying a guess - never the email/password/token/response body.
        var loginUrl = httpClient.BaseAddress is { } baseAddress ? new Uri(baseAddress, "auth/login") : (Uri?)null;

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/login", new LoginRequestDto { Email = email, Password = password }, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var body = await response.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions, cancellationToken);
                    if (body is null)
                    {
                        logger.LogWarning("Login request to {Url} returned {StatusCode} with an empty body.", loginUrl, (int)response.StatusCode);
                        return new CloudLoginResult(false, null, "Login succeeded but the response body was empty.");
                    }

                    logger.LogInformation("Login request to {Url} succeeded ({StatusCode}).", loginUrl, (int)response.StatusCode);
                    return new CloudLoginResult(true, body, null);
                }
                catch (JsonException ex)
                {
                    // The cloud reached us and said "success" (2xx) but the body
                    // doesn't match LoginResponseDto - a response-contract
                    // mismatch, NOT a network/connectivity failure. Must never be
                    // reported as "cannot reach the cloud".
                    logger.LogWarning(ex, "Login request to {Url} returned {StatusCode} but the response body could not be parsed as LoginResponseDto.", loginUrl, (int)response.StatusCode);
                    return new CloudLoginResult(false, null, $"Login response could not be parsed: {ex.Message}", IsNetworkFailure: false);
                }
            }

            // Reached the cloud; it authoritatively rejected the request (e.g. 401
            // invalid credentials, or a disabled account) - NOT a network failure,
            // so AuthenticationService must never fall back to an offline cache here.
            var message = await SafeReadMessageAsync(response, cancellationToken);
            logger.LogInformation("Login request to {Url} was rejected by the cloud ({StatusCode}).", loginUrl, (int)response.StatusCode);
            return new CloudLoginResult(false, null, message ?? $"Login failed ({(int)response.StatusCode}).", IsNetworkFailure: false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Network error reaching {Url} for login ({ExceptionType}).", loginUrl, ex.GetType().Name);
            return new CloudLoginResult(false, null, $"Network error contacting the cloud API: {ex.Message}", IsNetworkFailure: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Login request to {Url} timed out.", loginUrl);
            return new CloudLoginResult(false, null, "Login request timed out.", IsNetworkFailure: true);
        }
    }

    public Task<IReadOnlyList<ChillingCentreDto>> GetCentresAsync(string accessToken, CancellationToken cancellationToken) =>
        GetListAsync<ChillingCentreDto>("centres", accessToken, cancellationToken);

    public Task<IReadOnlyList<SourceDto>> GetSourcesAsync(string accessToken, CancellationToken cancellationToken) =>
        GetListAsync<SourceDto>("sources", accessToken, cancellationToken);

    public Task<IReadOnlyList<VehicleDto>> GetVehiclesAsync(string accessToken, CancellationToken cancellationToken) =>
        GetListAsync<VehicleDto>("vehicles", accessToken, cancellationToken);

    public Task<IReadOnlyList<QualityRuleDto>> GetQualityRulesAsync(string accessToken, CancellationToken cancellationToken) =>
        GetListAsync<QualityRuleDto>("quality-rules", accessToken, cancellationToken);

    public async Task<CloudCreateReceptionResult> CreateReceptionAsync(
        string accessToken, CreateReceptionRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "reception")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                var dto = JsonSerializer.Deserialize<ReceptionTransactionDto>(rawBody, JsonOptions)
                    ?? throw new InvalidOperationException("POST /reception returned 201 with an unparseable body.");
                var outcome = HttpResponseClassifier.Classify(response.StatusCode, dto.Outcome);
                return new CloudCreateReceptionResult(outcome, dto, null);
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var conflict = TryDeserialize<ReceptionConflictResponseDto>(rawBody);
                return new CloudCreateReceptionResult(
                    CloudReceptionOutcome.Conflict, null, conflict?.Message ?? "Conflict.", conflict?.ConflictingFields);
            }

            var classified = HttpResponseClassifier.Classify(response.StatusCode, null);
            var message = TryDeserialize<ReceptionConflictResponseDto>(rawBody)?.Message
                ?? $"POST /reception failed with status {(int)response.StatusCode}.";
            return new CloudCreateReceptionResult(classified, null, message);
        }
        catch (HttpRequestException ex)
        {
            return new CloudCreateReceptionResult(CloudReceptionOutcome.Retryable, null, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudCreateReceptionResult(CloudReceptionOutcome.Retryable, null, "Request timed out.");
        }
        catch (JsonException ex)
        {
            // A malformed/unparseable success body (truncated response, unexpected
            // shape, a proxy error page returned with a 201, etc.) must not leave
            // this outbox row stuck in PROCESSING forever (see SyncEngineService -
            // an uncaught exception here previously propagated all the way out of
            // a sync tick with the row already claimed). Treated as retryable, not
            // terminal: the next attempt may succeed once the transient condition
            // clears, and Postgres/the cloud already has (or will have) the
            // authoritative row either way - this classification only affects how
            // the LOCAL outbox retries, never cloud-side correctness.
            return new CloudCreateReceptionResult(CloudReceptionOutcome.Retryable, null, $"Malformed response body: {ex.Message}");
        }
    }

    /// <summary>
    /// Unlike CreateReceptionAsync, POST /reception/:id/override has no
    /// idempotency-key mechanism in the verified cloud contract (context.md) -
    /// a retried request cannot be distinguished from a genuine second attempt
    /// by a different actor. This method therefore only classifies the
    /// response (never throws for a non-success status) so SyncEngineService
    /// can apply its own, deliberately conservative retry policy - see
    /// CLAUDE.md "Architecture Decisions" (Manager Override Sync) for why a
    /// terminal/4xx response here is treated as FAILED-for-manual-review
    /// rather than auto-retried or auto-assumed-successful.
    /// </summary>
    public async Task<CloudOverrideResult> OverrideReceptionAsync(
        string accessToken, int cloudTransactionId, OverrideReceptionRequestDto request, CancellationToken cancellationToken)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"reception/{cloudTransactionId}/override")
            {
                Content = JsonContent.Create(request, options: JsonOptions),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<ReceptionTransactionDto>(JsonOptions, cancellationToken);
                return dto is null
                    ? new CloudOverrideResult(CloudMutationOutcome.Retryable, null, "Override succeeded but the response body was empty.")
                    : new CloudOverrideResult(CloudMutationOutcome.Success, dto, null);
            }

            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = TryDeserialize<ReceptionConflictResponseDto>(rawBody)?.Message
                ?? $"Override failed with status {(int)response.StatusCode}.";

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new CloudOverrideResult(CloudMutationOutcome.AuthRetryable, null, message);
            }
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                return new CloudOverrideResult(CloudMutationOutcome.Retryable, null, message);
            }
            return new CloudOverrideResult(CloudMutationOutcome.Terminal, null, message);
        }
        catch (HttpRequestException ex)
        {
            return new CloudOverrideResult(CloudMutationOutcome.Retryable, null, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudOverrideResult(CloudMutationOutcome.Retryable, null, "Request timed out.");
        }
        catch (JsonException ex)
        {
            return new CloudOverrideResult(CloudMutationOutcome.Retryable, null, $"Malformed response body: {ex.Message}");
        }
    }

    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(string accessToken, int? centreId, CancellationToken cancellationToken)
    {
        var route = centreId is null ? "dashboard/summary" : $"dashboard/summary?centreId={centreId.Value}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, route);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardSummaryDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GET /dashboard/summary returned an unparseable body.");
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string route, string accessToken, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, route);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, cancellationToken);
        return list ?? [];
    }

    private static async Task<string?> SafeReadMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return TryDeserialize<ReceptionConflictResponseDto>(body)?.Message;
        }
        catch
        {
            return null;
        }
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
