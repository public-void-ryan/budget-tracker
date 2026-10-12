using System.Buffers.Text;
using System.Text;
using BudgetTracker.Api.Paging;

namespace BudgetTracker.Api.Tests.Paging;

public class TransactionCursorTests
{
    [Fact]
    public void A_cursor_round_trips()
    {
        var original = new TransactionCursor(new DateOnly(2026, 10, 1), Guid.CreateVersion7());

        Assert.True(TransactionCursor.TryDecode(original.Encode(), out var decoded));
        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(9999, 12, 31)]
    [InlineData(2024, 2, 29)]
    public void Boundary_dates_round_trip(int year, int month, int day)
    {
        var original = new TransactionCursor(new DateOnly(year, month, day), Guid.Empty);

        Assert.True(TransactionCursor.TryDecode(original.Encode(), out var decoded));
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void An_encoded_cursor_is_url_safe()
    {
        var encoded = new TransactionCursor(new DateOnly(2026, 10, 1), Guid.CreateVersion7()).Encode();

        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);
    }

    private static string Encode(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!!")]
    [InlineData("aGVsbG8")] // valid base64url, decodes to "hello"
    public void Malformed_text_is_rejected(string? text)
    {
        Assert.False(TransactionCursor.TryDecode(text, out var cursor));
        Assert.Equal(default, cursor);
    }

    [Theory]
    [InlineData("2026-10-01")]                                              // no id
    [InlineData("2026-10-01|")]                                             // empty id
    [InlineData("|01234567-89ab-cdef-0123-456789abcdef")]                   // no date
    [InlineData("2026-13-01|01234567-89ab-cdef-0123-456789abcdef")]         // month 13
    [InlineData("2026-02-30|01234567-89ab-cdef-0123-456789abcdef")]         // not a real day
    [InlineData("26-10-01|01234567-89ab-cdef-0123-456789abcdef")]           // wrong date shape
    [InlineData("2026-10-01|not-a-guid")]
    [InlineData("2026-10-01|01234567-89ab-cdef-0123-456789abcdef|extra")]   // three parts
    [InlineData("2026-10-01|0123456789abcdef0123456789abcdef")]             // guid without hyphens
    [InlineData("2026-10-01|{01234567-89ab-cdef-0123-456789abcdef}")]       // braces
    public void Well_encoded_text_with_the_wrong_content_is_rejected(string content)
    {
        Assert.False(TransactionCursor.TryDecode(Encode(content), out _));
    }

    [Fact]
    public void Bytes_that_are_not_valid_utf8_are_rejected_not_thrown()
    {
        var text = Base64Url.EncodeToString(new byte[] { 0xFF, 0xFE, 0xFD, 0x80, 0x81 });

        Assert.False(TransactionCursor.TryDecode(text, out _));
    }

    [Fact]
    public void An_oversized_cursor_is_rejected_before_it_is_decoded()
    {
        Assert.False(TransactionCursor.TryDecode(new string('A', 10_000), out _));
    }

    [Fact]
    public void Arbitrary_input_never_throws()
    {
        // Cursors come from the query string, so anything can arrive.
        var random = new Random(12345);
        var alphabets = new[]
        {
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=",
            "!@#$%^&*()|\\ \t\n日本語🔑",
        };

        for (var i = 0; i < 2_000; i++)
        {
            var alphabet = alphabets[i % alphabets.Length];
            var text = new string(Enumerable.Range(0, random.Next(1, 150)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            var exception = Record.Exception(() => TransactionCursor.TryDecode(text, out _));

            Assert.Null(exception);
        }
    }
}
