using System.Buffers.Text;
using System.Security.Cryptography;

namespace BudgetTracker.Api.Auth;

/// <summary>
/// Opaque bearer tokens: 32 random bytes, base64url encoded. Only the SHA-256 hash is stored.
/// A fast hash is fine here, unlike for passwords, because the input is 256 bits of randomness
/// and cannot be guessed or brute-forced offline.
/// </summary>
public static class SessionTokens
{
    private const int TokenBytes = 32;
    private const int TokenLength = 43; // 32 bytes as unpadded base64url

    public static (string Token, byte[] Hash) Generate()
    {
        var raw = RandomNumberGenerator.GetBytes(TokenBytes);
        return (Base64Url.EncodeToString(raw), SHA256.HashData(raw));
    }

    /// <summary>The hash a presented token would have been stored under, or null if malformed.</summary>
    public static byte[]? Hash(string token)
    {
        // IsValid first: TryDecodeFromChars throws FormatException on illegal characters
        // rather than returning false, which would turn a bad token into a 500.
        if (token.Length != TokenLength || !Base64Url.IsValid(token))
        {
            return null;
        }

        Span<byte> raw = stackalloc byte[TokenBytes + 3];
        if (!Base64Url.TryDecodeFromChars(token, raw, out var written) || written != TokenBytes)
        {
            return null;
        }

        return SHA256.HashData(raw[..written]);
    }
}
