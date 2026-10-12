using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Validation;

public static class TextRules
{
    /// <summary>
    /// Required single-line text (a name, a description): trimmed, 1 to <paramref name="maxLength"/> characters, no
    /// control characters (a name with a newline in it is junk, not intent). Length counts
    /// Unicode characters, matching how the database counts them, so 100 emoji fit a
    /// varchar(100) even though they are 200 UTF-16 units.
    /// </summary>
    public static (string Value, IReadOnlyList<ValidationError> Errors) RequiredText(
        string? raw, string field, int maxLength)
    {
        var value = (raw ?? "").Trim();

        if (value.Length == 0)
        {
            return (value, [new(field, $"{field}.required", $"{Capitalized(field)} is required.")]);
        }

        if (value.EnumerateRunes().Count() > maxLength)
        {
            return (value, [new(field, $"{field}.too_long", $"{Capitalized(field)} must be at most {maxLength} characters.")]);
        }

        if (value.Any(char.IsControl))
        {
            return (value, [new(field, $"{field}.invalid", $"{Capitalized(field)} must not contain control characters.")]);
        }

        return (value, []);
    }

    private static string Capitalized(string field) => char.ToUpperInvariant(field[0]) + field[1..];
}
