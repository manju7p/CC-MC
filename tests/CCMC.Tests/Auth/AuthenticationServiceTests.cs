using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Contracts.Auth;
using CCMC.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CCMC.Tests.Auth;

/// <summary>
/// Covers AuthenticationService.LoginAsync's decision tree in isolation
/// (fake ICloudApiClient/ISessionStore/IOfflineCredentialStore) - added
/// during the login-bug investigation (Windows app showed "Cannot reach the
/// cloud..." even though Swagger login succeeded; root cause was an
/// http/https scheme mismatch in appsettings.json, fixed separately). These
/// tests prove the classification logic itself was always correct - a
/// genuine online rejection never falls back to offline, only a real
/// network failure does - and that the offline fallback still works.
/// </summary>
public class AuthenticationServiceTests
{
    private static readonly AuthenticatedUserDto SampleUser = new()
    {
        Id = 3,
        Email = "operator1@ccmc.local",
        FullName = "Bangalore Operator",
        Roles = ["Operator"],
        Permissions = ["RECEPTION_CREATE"],
        CentreAccess = new CentreAccessSummaryDto { AllCentres = false, CentreIds = [1] },
    };

    private static AuthenticationService CreateService(
        FakeCloudApiClient cloudApiClient, FakeSessionStore sessionStore, FakeOfflineCredentialStore offlineStore) =>
        new(cloudApiClient, sessionStore, offlineStore, new FakeAuditLogRepository(), new FakeClock(),
            NullLogger<AuthenticationService>.Instance);

    [Fact]
    public async Task LoginAsync_CloudAccepts_SetsOnlineSessionWithRealToken()
    {
        var cloudApiClient = new FakeCloudApiClient
        {
            Result = new CloudLoginResult(true, new LoginResponseDto { AccessToken = "jwt-token-value", User = SampleUser }, null),
        };
        var sessionStore = new FakeSessionStore();
        var offlineStore = new FakeOfflineCredentialStore();
        var service = CreateService(cloudApiClient, sessionStore, offlineStore);

        var result = await service.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.IsOffline);
        Assert.NotNull(sessionStore.Current);
        Assert.Equal("jwt-token-value", sessionStore.Current!.AccessToken);
        Assert.False(sessionStore.Current.IsOffline);
        Assert.False(offlineStore.VerifyWasCalled); // an online success never falls back to the offline path
    }

    [Fact]
    public async Task LoginAsync_CloudRejects401_FailsWithoutAttemptingOfflineFallback()
    {
        // The cloud was genuinely reached and said no (e.g. wrong password) -
        // this must never be second-guessed by a possibly-stale offline cache.
        var cloudApiClient = new FakeCloudApiClient
        {
            Result = new CloudLoginResult(false, null, "Invalid credentials", IsNetworkFailure: false),
        };
        var sessionStore = new FakeSessionStore();
        var offlineStore = new FakeOfflineCredentialStore();
        var service = CreateService(cloudApiClient, sessionStore, offlineStore);

        var result = await service.LoginAsync("operator1@ccmc.local", "wrong-password", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Invalid credentials", result.ErrorMessage);
        Assert.Null(sessionStore.Current);
        Assert.False(offlineStore.VerifyWasCalled);
    }

    [Fact]
    public async Task LoginAsync_NetworkFailure_NoCachedCredential_ReturnsExactUserFacingMessage()
    {
        // Reproduces the reported bug's symptom directly: a genuine network
        // failure (e.g. the http/https scheme mismatch this investigation
        // found) with no prior successful offline login for this account.
        var cloudApiClient = new FakeCloudApiClient
        {
            Result = new CloudLoginResult(false, null, "Network error contacting the cloud API: some transport error", IsNetworkFailure: true),
        };
        var sessionStore = new FakeSessionStore();
        var offlineStore = new FakeOfflineCredentialStore { VerifyResult = null };
        var service = CreateService(cloudApiClient, sessionStore, offlineStore);

        var result = await service.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Cannot reach the cloud, and no valid cached offline credential exists for this account.", result.ErrorMessage);
        Assert.True(offlineStore.VerifyWasCalled);
        Assert.Null(sessionStore.Current);
    }

    [Fact]
    public async Task LoginAsync_NetworkFailure_ValidCachedCredential_FallsBackToOfflineSession()
    {
        var cloudApiClient = new FakeCloudApiClient
        {
            Result = new CloudLoginResult(false, null, "Network error contacting the cloud API: some transport error", IsNetworkFailure: true),
        };
        var sessionStore = new FakeSessionStore();
        var offlineStore = new FakeOfflineCredentialStore
        {
            VerifyResult = new OfflineIdentitySnapshot(3, "operator1@ccmc.local", "Bangalore Operator", ["Operator"], ["RECEPTION_CREATE"], false, [1]),
        };
        var service = CreateService(cloudApiClient, sessionStore, offlineStore);

        var result = await service.LoginAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.IsOffline);
        Assert.NotNull(sessionStore.Current);
        Assert.True(sessionStore.Current!.IsOffline);
        Assert.Equal(string.Empty, sessionStore.Current.AccessToken); // never a usable token in an offline session
    }

    private sealed class FakeCloudApiClient : ICloudApiClient
    {
        public required CloudLoginResult Result { get; init; }

        public Task<CloudLoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<IReadOnlyList<CCMC.Contracts.Dtos.ChillingCentreDto>> GetCentresAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CCMC.Contracts.Dtos.SourceDto>> GetSourcesAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CCMC.Contracts.Dtos.VehicleDto>> GetVehiclesAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CCMC.Contracts.Dtos.QualityRuleDto>> GetQualityRulesAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CCMC.Contracts.Dtos.RateFormulaSettingsDto>> GetRateFormulaSettingsAsync(string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CloudCreateReceptionResult> CreateReceptionAsync(
            string accessToken, CCMC.Contracts.Dtos.CreateReceptionRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CloudOverrideResult> OverrideReceptionAsync(
            string accessToken, int cloudTransactionId, CCMC.Contracts.Dtos.OverrideReceptionRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<CCMC.Contracts.Dtos.DashboardSummaryDto> GetDashboardSummaryAsync(
            string accessToken, int? centreId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public Session? Current { get; private set; }
        public void Set(Session session) => Current = session;
        public void Clear() => Current = null;
    }

    private sealed class FakeOfflineCredentialStore : IOfflineCredentialStore
    {
        public OfflineIdentitySnapshot? VerifyResult { get; init; }
        public bool VerifyWasCalled { get; private set; }

        public Task SaveAsync(string email, string password, OfflineIdentitySnapshot snapshot, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OfflineIdentitySnapshot?> VerifyAsync(string email, string password, CancellationToken cancellationToken)
        {
            VerifyWasCalled = true;
            return Task.FromResult(VerifyResult);
        }
    }

    private sealed class FakeAuditLogRepository : IAuditLogRepository
    {
        public Task RecordAsync(AuditLogEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> ListRecentAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
    }
}
