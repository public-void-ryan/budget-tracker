using System.Globalization;
using System.Text.RegularExpressions;
using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Validation;

public static partial class MonthRules
{
    [GeneratedRegex(@"^\d{4}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>
    /// A calendar month written exactly yyyy-MM, as the first day of that month. Nothing looser:
    /// "2026-1", "202610" and "2026-10-01" are all mistakes worth reporting, not guessing at.
    /// </summary>
    public static (DateOnly? Month, ValidationError? Error) Parse(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, new(field, $"{field}.required", $"{Capitalized(field)} is required, written yyyy-MM."));
        }

        if (!Shape().IsMatch(raw)
            || !DateOnly.TryParseExact(raw + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return (null, new(field, $"{field}.invalid", $"{Capitalized(field)} must be a real month written yyyy-MM."));
        }

        return (month, null);
    }

    public static string Format(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string Capitalized(string field) => char.ToUpperInvariant(field[0]) + field[1..];
}
