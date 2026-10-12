using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

public enum PasswordVerification
{
    Invalid,
    Valid,

    /// <summary>Correct password, but the stored hash uses outdated parameters: re-hash it.</summary>
    ValidNeedsRehash,
}

public interface IPasswordService
{
    string Hash(string password);

    PasswordVerification Verify(string storedHash, string password);

    /// <summary>
    /// Spends the same work as a real verification and always fails. Used when the email is
    /// unknown, so login timing does not reveal which emails are registered.
    /// </summary>
    void VerifyAgainstDummy(string password);
}

/// <summary>
/// Thin wrapper over ASP.NET's PasswordHasher (PBKDF2-HMAC-SHA512, per-hash random salt, the
/// parameters encoded in the hash so they can be raised later without invalidating old ones).
/// </summary>
public sealed class PasswordService : IPasswordService
{
    // PasswordHasher<TUser> only needs a TUser for custom subclasses; the default ignores it.
    private sealed class Principal;

    private static readonly Principal Subject = new();

    private readonly PasswordHasher<Principal> _hasher;
    private readonly string _dummyHash;

    public PasswordService(IOptions<PasswordHasherOptions>? options = null)
    {
        _hasher = new PasswordHasher<Principal>(options);
        _dummyHash = _hasher.HashPassword(Subject, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => _hasher.HashPassword(Subject, password);

    public PasswordVerification Verify(string storedHash, string password) =>
        _hasher.VerifyHashedPassword(Subject, storedHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Valid,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.ValidNeedsRehash,
            _ => PasswordVerification.Invalid,
        };

    public void VerifyAgainstDummy(string password) =>
        _hasher.VerifyHashedPassword(Subject, _dummyHash, password);
}
