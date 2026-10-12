using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Validation;

public static class EnumRules
{
    /// <summary>
    /// Strict on purpose: the canonical spelling only (after trimming), so a typo is an error,
    /// not a guess. It matches the exact names instead of using Enum.TryParse, which also accepts
    /// numbers and comma-separated lists whose values it ORs together, so that for a non-flags
    /// enum "Checking, Savings" would have been accepted as a valid value.
    /// </summary>
    public static (TEnum Value, IReadOnlyList<ValidationError> Errors) ParseExact<TEnum>(string? raw, string field)
        where TEnum : struct, Enum
    {
        var label = char.ToUpperInvariant(field[0]) + field[1..];
        var text = raw?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return (default, [new(field, $"{field}.required", $"{label} is required.")]);
        }

        var names = Enum.GetNames<TEnum>();
        if (!Array.Exists(names, name => name == text))
        {
            return (default, [new(field, $"{field}.invalid", $"{label} must be one of: {string.Join(", ", names)}.")]);
        }

        return (Enum.Parse<TEnum>(text), []);
    }
}
