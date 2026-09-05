using System.Runtime.Versioning;
using CCMC.Application.Abstractions;
using CCMC.Infrastructure.Auth;
using CCMC.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CCMC.Tests.Auth;

/// <summary>
/// Exercises the real OfflineCredentialStore, including real DPAPI protect/
/// unprotect (CurrentUser scope) and real Argon2id hashing - no fakes for
/// the cryptography itself, since that IS the behavior being verified. Only
/// runs where DPAPI is available (Windows - this app's only real target).
/// </summary>
[SupportedOSPlatform("windows")]
public class OfflineCredentialStoreTests : IDisposable
{
    // Fresh SQLite database per test (see DeviceManagerTests for why -
    // these assert on "no cached credential exists" in places, which would
    // be order-dependent under a shared IClassFixture).
    private readonly SqliteTestFixture _fixture = new();
    private readonly OfflineCredentialStore _store;

    public OfflineCredentialStoreTests()
    {
        _store = new OfflineCredentialStore(_fixture.ConnectionFactory, NullLogger<OfflineCredentialStore>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private static OfflineIdentitySnapshot SampleSnapshot(string email = "operator1@ccmc.local") => new(
        UserId: 3, Email: email, FullName: "Bangalore Operator",
        Roles: ["Operator"], Permissions: ["RECEPTION_VIEW", "RECEPTION_CREATE"],
        AllCentres: false, CentreIds: [1]);

    [Fact]
    public async Task VerifyAsync_CorrectPassword_ReturnsCachedSnapshot()
    {
        await _store.SaveAsync("operator1@ccmc.local", "Operator@12345", SampleSnapshot(), CancellationToken.None);

        var result = await _store.VerifyAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.UserId);
        Assert.Equal("Bangalore Operator", result.FullName);
        Assert.Equal(["Operator"], result.Roles);
        Assert.False(result.AllCentres);
        Assert.Equal([1], result.CentreIds);
    }

    [Fact]
    public async Task VerifyAsync_WrongPassword_ReturnsNull()
    {
        await _store.SaveAsync("operator1@ccmc.local", "Operator@12345", SampleSnapshot(), CancellationToken.None);

        var result = await _store.VerifyAsync("operator1@ccmc.local", "WrongPassword", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyAsync_NoCachedCredential_ReturnsNull_DoesNotThrow()
    {
        var result = await _store.VerifyAsync("nobody@ccmc.local", "AnyPassword", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_CalledTwiceForSameEmail_RefreshesCachedSnapshot()
    {
        await _store.SaveAsync("operator1@ccmc.local", "Operator@12345", SampleSnapshot(), CancellationToken.None);

        var updatedSnapshot = new OfflineIdentitySnapshot(
            UserId: 3, Email: "operator1@ccmc.local", FullName: "Bangalore Operator",
            Roles: ["Operator", "Manager"], Permissions: ["RECEPTION_VIEW", "RECEPTION_CREATE", "RECEPTION_OVERRIDE"],
            AllCentres: true, CentreIds: []);
        await _store.SaveAsync("operator1@ccmc.local", "NewPassword@123", updatedSnapshot, CancellationToken.None);

        // Old password no longer verifies - the cache was replaced, not appended to.
        Assert.Null(await _store.VerifyAsync("operator1@ccmc.local", "Operator@12345", CancellationToken.None));

        var result = await _store.VerifyAsync("operator1@ccmc.local", "NewPassword@123", CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.AllCentres);
        Assert.Equal(["Operator", "Manager"], result.Roles);
    }

    [Fact]
    public async Task SavedBlob_IsNotPlaintext_PasswordAndRolesNotVisibleInStorage()
    {
        await _store.SaveAsync("operator1@ccmc.local", "Operator@12345", SampleSnapshot(), CancellationToken.None);

        await using var connection = _fixture.ConnectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_blob FROM offline_credentials WHERE email = 'operator1@ccmc.local';";
        var storedBlob = (string)(await command.ExecuteScalarAsync())!;

        // DPAPI-protected - must not contain the plaintext password or role
        // names anywhere in the stored representation.
        Assert.DoesNotContain("Operator@12345", storedBlob);
        Assert.DoesNotContain("Bangalore Operator", storedBlob);
    }

    [Fact]
    public async Task VerifyAsync_DifferentEmailsAreIndependent()
    {
        await _store.SaveAsync("operator1@ccmc.local", "Pass1", SampleSnapshot("operator1@ccmc.local"), CancellationToken.None);
        await _store.SaveAsync("operator2@ccmc.local", "Pass2", SampleSnapshot("operator2@ccmc.local"), CancellationToken.None);

        Assert.Null(await _store.VerifyAsync("operator1@ccmc.local", "Pass2", CancellationToken.None));
        Assert.NotNull(await _store.VerifyAsync("operator1@ccmc.local", "Pass1", CancellationToken.None));
        Assert.NotNull(await _store.VerifyAsync("operator2@ccmc.local", "Pass2", CancellationToken.None));
    }
}
