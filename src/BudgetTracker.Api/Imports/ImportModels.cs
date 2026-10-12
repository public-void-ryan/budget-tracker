namespace BudgetTracker.Api.Imports;

/// <summary>
/// Named date layouts instead of raw format strings, so a caller picks from a short list and
/// cannot supply an arbitrary format.
/// </summary>
public enum DateFormatName
{
    Iso,       // 2026-10-31
    Us,        // 10/31/2026 (1 or 2 digits)
    Eu,        // 31/10/2026 (1 or 2 digits)
    IsoSlash,  // 2026/10/31
    EuDots,    // 31.10.2026
}

/// <param name="DateColumn">Header of the date column; detected from common names when null.</param>
/// <param name="DescriptionColumn">Header of the description column; detected when null.</param>
/// <param name="AmountColumn">Header of a single signed amount column; detected when null.</param>
/// <param name="DebitColumn">Header of a money-out column, for files that split debit and credit.</param>
/// <param name="CreditColumn">Header of a money-in column, for files that split debit and credit.</param>
public sealed record ImportOptions(
    string? DateColumn = null,
    string? DescriptionColumn = null,
    string? AmountColumn = null,
    string? DebitColumn = null,
    string? CreditColumn = null,
    DateFormatName DateFormat = DateFormatName.Iso);

/// <summary>One valid CSV row. <paramref name="Amount"/> is exactly what the file said, before any sign inversion.</summary>
public sealed record ParsedRow(int Row, DateOnly Date, string Description, decimal Amount);

/// <param name="Row">1-based data row (the header is not counted); 0 for a problem with the file or header as a whole.</param>
public sealed record ImportError(int Row, string Field, string Code, string Message);

public sealed record ParseResult(
    IReadOnlyList<ParsedRow> Rows,
    IReadOnlyList<ImportError> Errors,
    bool ErrorsTruncated)
{
    public bool Succeeded => Errors.Count == 0;
}
