using System.Text;
using System.Text.RegularExpressions;

namespace BudgetTracker.Api.Text;

/// <summary>
/// How transaction descriptions are compared. Import deduplication and categorization rules both
/// need "the same text, ignoring case and how it happens to be typed", and they must agree.
/// </summary>
public static partial class DescriptionText
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Trimmed, Unicode-composed (so "café" typed with a combining accent equals the precomposed
    /// one) and lowercased with the invariant culture: lowercasing with the machine's culture
    /// would turn "I" into a dotless "ı" on a Turkish-locale server.
    ///
    /// This feeds the import hash, which is a contract with data already stored. Do not change
    /// it; the golden hash tests pin it.
    /// </summary>
    public static string Normalize(string text) =>
        text.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>
    /// <see cref="Normalize"/> plus runs of whitespace collapsed to one space, for matching typed
    /// patterns against descriptions. Kept separate so that it cannot alter import hashes.
    /// </summary>
    public static string NormalizeForMatching(string text) => Whitespace().Replace(Normalize(text), " ");
}
