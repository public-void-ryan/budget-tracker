using BudgetTracker.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Tests.Auth;

public class PasswordServiceTests
{
    private readonly PasswordService _service = new();

    [Fact]
    public void A_hashed_password_verifies()
    {
        var hash = _service.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Valid, _service.Verify(hash, "correct horse battery staple"));
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
    {
        var hash = _service.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Invalid, _service.Verify(hash, "correct horse battery stapl"));
    }

    [Fact]
    public void Hashes_are_salted_so_equal_passwords_hash_differently()
    {
        Assert.NotEqual(_service.Hash("same password 123"), _service.Hash("same password 123"));
    }

    [Fact]
    public void The_hash_does_not_contain_the_password()
    {
        Assert.DoesNotContain("correct horse", _service.Hash("correct horse battery staple"));
    }

    [Fact]
    public void The_hash_fits_the_password_hash_column()
    {
        // users.password_hash is varchar(255); the longest accepted password must still fit.
        var hash = _service.Hash(new string('x', CredentialRules.PasswordMaxLength));

        Assert.True(hash.Length <= 255, $"hash was {hash.Length} characters");
    }

    [Fact]
    public void Unicode_passwords_round_trip()
    {
        const string password = "pässwörd-日本語-🔑-1234";
        var hash = _service.Hash(password);

        Assert.Equal(PasswordVerification.Valid, _service.Verify(hash, password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hash")]
    [InlineData("AQAAAAEAACcQAAAAEA==")] // base64, but truncated
    public void A_malformed_stored_hash_is_invalid_not_an_exception(string storedHash)
    {
        Assert.Equal(PasswordVerification.Invalid, _service.Verify(storedHash, "anything at all"));
    }

    [Fact]
    public void A_hash_made_with_fewer_iterations_is_valid_but_flagged_for_rehash()
    {
        // Raising the iteration count later must not lock existing users out: their old hash
        // still verifies, and is upgraded the next time they log in.
        var old = new PasswordService(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }));
        var hash = old.Hash("correct horse battery staple");

        Assert.Equal(
            PasswordVerification.ValidNeedsRehash,
            _service.Verify(hash, "correct horse battery staple"));
        Assert.Equal(
            PasswordVerification.Invalid,
            _service.Verify(hash, "a different password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("correct horse battery staple")]
    [InlineData("pässwörd-日本語-1234")]
    public void The_dummy_verification_runs_for_any_input(string password)
    {
        // The timing equalization itself is not asserted: wall-clock assertions are flaky.
        // This pins that the unknown-email path can never throw or accept a login.
        _service.VerifyAgainstDummy(password);
    }
}
