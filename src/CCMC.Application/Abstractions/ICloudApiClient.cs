using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;

namespace CCMC.Application.Abstractions;

/// <summary>
/// Classification of a POST /reception attempt, mirroring the cloud's own
/// documented response contract (context.md "Cloud Responsibilities" /
/// classifyHttpResponse() design): both Created and Duplicate are success -
/// the cloud has exactly one row for this idempotency key either way.
/// AuthRetryable is handled internally by the client (re-authenticate and
/// retry once) and should never actually surface to a caller in practice,
/// but is modeled explicitly rather than silently folded into Retryable.
/// </summary>
public enum CloudReceptionOutcome
{
    Created,
    Duplicate,
    Conflict,
    AuthRetryable,
    Retryable,
    Terminal,
}

public sealed record CloudCreateReceptionResult(
    CloudReceptionOutcome Outcome,
    ReceptionTransactionDto? Transaction,
    string? ErrorMessage,
    IReadOnlyList<string>? ConflictingFields = null);

/// <summary>
/// IsNetworkFailure distinguishes "the cloud could not be reached at all"
/// (connection refused, DNS failure, timeout) from "the cloud was reached
/// and authoritatively rejected the request" (401 invalid credentials, a
/// disabled account, etc.). AuthenticationService uses this distinction to
/// decide whether an offline-credential fallback is even appropriate - a
/// genuine online rejection must never be second-guessed by a possibly-stale
/// local cache (see CLAUDE.md "Decisions Pending" / the offline-login design).
/// </summary>
public sealed record CloudLoginResult(bool Success, LoginResponseDto? Response, string? ErrorMessage, bool IsNetworkFailure = false);

/// <summary>
/// Classification for a single-outcome cloud mutation (no created/duplicate
/// distinction - used for POST /reception/:id/override, which has no
/// idempotency-key mechanism unlike POST /reception - see
/// CLAUDE.md "Architecture Decisions" for the documented contract gap this
/// implies for retry safety).
/// </summary>
public enum CloudMutationOutcome
{
    Success,
    AuthRetryable,
    Retryable,
    Terminal,
}

public sealed record CloudOverrideResult(CloudMutationOutcome Outcome, ReceptionTransactionDto? Transaction, string? ErrorMessage);

/// <summary>
/// A 403 (the caller's role lacks SOURCE_CREATE/VEHICLE_CREATE) is a Terminal
/// outcome, same as any other authoritative rejection - see
/// CloudMutationOutcome's existing OverrideReceptionAsync usage for the same
/// status-code classification this mirrors. The caller (SourcesWindow/
/// VehiclesWindow) surfaces ErrorMessage directly rather than assuming
/// success.
/// </summary>
public sealed record CloudCreateSourceResult(CloudMutationOutcome Outcome, SourceDto? Source, string? ErrorMessage);

public sealed record CloudCreateVehicleResult(CloudMutationOutcome Outcome, VehicleDto? Vehicle, string? ErrorMessage);

/// <summary>PUT /rate-formula-settings - gated server-side by RATE_FORMULA_CONFIGURE (Manager/Admin) and centre scoping (see RateFormulaSettingsService.UpsertAsync). A 403 (permission denied or out-of-scope centre) is Terminal, same classification as CloudCreateSourceResult/CloudCreateVehicleResult.</summary>
public sealed record CloudUpdateRateFormulaSettingsResult(CloudMutationOutcome Outcome, RateFormulaSettingsDto? Settings, string? ErrorMessage);

/// <summary>
/// The Windows app's only path to the cloud. Implemented by
/// CCMC.Infrastructure's HttpCloudApiClient against the existing NestJS API
/// contract documented in context.md - NOT a new/invented contract. No
/// endpoint here may be called that isn't already verified to exist.
/// </summary>
public interface ICloudApiClient
{
    Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChillingCentreDto>> GetCentresAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceDto>> GetSourcesAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<VehicleDto>> GetVehiclesAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<QualityRuleDto>> GetQualityRulesAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<RateFormulaSettingsDto>> GetRateFormulaSettingsAsync(string accessToken, CancellationToken cancellationToken);

    Task<CloudCreateReceptionResult> CreateReceptionAsync(
        string accessToken, CreateReceptionRequestDto request, CancellationToken cancellationToken);

    Task<CloudOverrideResult> OverrideReceptionAsync(
        string accessToken, int cloudTransactionId, OverrideReceptionRequestDto request, CancellationToken cancellationToken);

    /// <summary>POST /sources - gated server-side by SOURCE_CREATE (Manager/Admin only, per DevelopmentSeeder's role grants). Never assume success client-side; the server remains the real authorization boundary.</summary>
    Task<CloudCreateSourceResult> CreateSourceAsync(string accessToken, CreateSourceRequestDto request, CancellationToken cancellationToken);

    /// <summary>POST /vehicles - gated server-side by VEHICLE_CREATE (Manager/Admin only, per DevelopmentSeeder's role grants).</summary>
    Task<CloudCreateVehicleResult> CreateVehicleAsync(string accessToken, CreateVehicleRequestDto request, CancellationToken cancellationToken);

    /// <summary>PUT /rate-formula-settings - gated server-side by RATE_FORMULA_CONFIGURE (Manager/Admin only) and centre scoping.</summary>
    Task<CloudUpdateRateFormulaSettingsResult> UpdateRateFormulaSettingsAsync(string accessToken, UpsertRateFormulaSettingsRequestDto request, CancellationToken cancellationToken);

    Task<DashboardSummaryDto> GetDashboardSummaryAsync(string accessToken, int? centreId, CancellationToken cancellationToken);
}
