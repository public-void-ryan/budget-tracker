using System.Buffers.Text;
using System.Security.Cryptography;
using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Tests.Auth;

public class SessionTokensTests
{
    [Fact]
    public void A_generated_token_hashes_back_to_the_stored_hash()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.Equal(hash, SessionTokens.Hash(token));
    }

    [Fact]
    public void The_stored_hash_is_a_sha256_of_the_random_bytes_not_of_the_token_text()
    {
        var (token, hash) = SessionTokens.Generate();

        Assert.Equal(32, hash.Length);
        Assert.Equal(SHA256.HashData(Base64Url.DecodeFromChars(token)), hash);
        Assert.NotEqual(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)), hash);
    }

    [Fact]
    public void Tokens_are_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => SessionTokens.Generate().Token).ToList();

        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")] // right length, not base64url
    public void Malformed_tokens_have_no_hash(string token)
    {
        Assert.Null(SessionTokens.Hash(token));
    }

    [Fact]
    public void A_token_of_the_wrong_length_is_rejected_even_if_it_is_valid_base64url()
    {
        Assert.Null(SessionTokens.Hash(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(33))));
    }
}
