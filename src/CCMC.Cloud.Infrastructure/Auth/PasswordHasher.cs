using Microsoft.AspNetCore.Identity;

namespace CCMC.Cloud.Infrastructure.Auth;

/// <summary>
/// Wraps ASP.NET Core Identity's PasswordHasher (PBKDF2, well-vetted,
/// zero-configuration) rather than adding a separate crypto package for the
/// cloud side - never a plaintext password is stored, this is the only
/// thing persisted for a user's credential.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>True if password matches the given hash.</summary>
    bool Verify(string hash, string password);
}

public sealed class PasswordHasherAdapter : IPasswordHasher
{
    // TUser is unused by the default PasswordHasher<T> implementation - a
    // throwaway `object` instance is the standard pattern when using it
    // outside ASP.NET Core Identity's full user-store system.
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object DummyUser = new();

    public string Hash(string password) => _hasher.HashPassword(DummyUser, password);

    public bool Verify(string hash, string password)
    {
        var result = _hasher.VerifyHashedPassword(DummyUser, hash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
