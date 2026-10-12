using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace BudgetTracker.Api.Paging;

/// <summary>
/// A position in the newest-first transaction list: the (posted_on, id) of the last item on
/// the previous page. Clients treat it as an opaque string. It is not signed: every query is
/// already scoped to the caller's own rows, so a forged cursor can only move the caller to a
/// different position in their own data. What matters is that a malformed one is rejected
/// cleanly, never an exception.
/// </summary>
public readonly record struct TransactionCursor(DateOnly PostedOn, Guid Id)
{
    private const int MaxEncodedLength = 128;
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{PostedOn.ToString(DateFormat, CultureInfo.InvariantCulture)}|{Id:D}")));

    public static bool TryDecode(string? text, out TransactionCursor cursor)
    {
        cursor = default;

        if (string.IsNullOrEmpty(text) || text.Length > MaxEncodedLength || !Base64Url.IsValid(text))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Strict.GetString(Base64Url.DecodeFromChars(text));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }

        var parts = decoded.Split('|');
        if (parts.Length != 2
            || !DateOnly.TryParseExact(parts[0], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var postedOn)
            || !Guid.TryParseExact(parts[1], "D", out var id))
        {
            return false;
        }

        cursor = new TransactionCursor(postedOn, id);
        return true;
    }
}
