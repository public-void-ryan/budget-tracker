using System.Globalization;
using System.Text.RegularExpressions;
using BudgetTracker.Api.Validation;
using CsvHelper;
using CsvHelper.Configuration;

namespace BudgetTracker.Api.Imports;

/// <summary>
/// Turns a bank's CSV export into validated rows, or into a complete list of what is wrong.
/// It never throws on bad input: a file that is not a CSV at all is reported like any other
/// problem. Nothing here touches the database.
/// </summary>
public static partial class CsvTransactionParser
{
    public const int MaxRows = 10_000;
    public const int MaxReportedErrors = 100;
    public const int DescriptionMaxLength = 500;

    private static readonly string[] DateNames = ["date", "posted date", "posting date", "transaction date", "trans date"];
    private static readonly string[] DescriptionNames = ["description", "memo", "payee", "details", "name", "narrative"];
    private static readonly string[] AmountNames = ["amount"];
    private static readonly string[] DebitNames = ["debit", "withdrawal", "withdrawals"];
    private static readonly string[] CreditNames = ["credit", "deposit", "deposits"];

    private static readonly Dictionary<DateFormatName, string> DateFormats = new()
    {
        [DateFormatName.Iso] = "yyyy-MM-dd",
        [DateFormatName.Us] = "M/d/yyyy",
        [DateFormatName.Eu] = "d/M/yyyy",
        [DateFormatName.IsoSlash] = "yyyy/M/d",
        [DateFormatName.EuDots] = "d.M.yyyy",
    };

    [GeneratedRegex(@"^[+-]?\d+(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();

    [GeneratedRegex(@"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupedNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record Columns(int Date, int Description, int? Amount, int? Debit, int? Credit);

    public static ParseResult Parse(TextReader reader, ImportOptions options)
    {
        var rows = new List<ParsedRow>();
        var errors = new List<ImportError>();
        var truncated = false;

        void Add(int row, string field, string code, string message)
        {
            if (errors.Count >= MaxReportedErrors)
            {
                truncated = true;
                return;
            }

            errors.Add(new ImportError(row, field, code, message));
        }

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            Mode = CsvMode.RFC4180,
            HasHeaderRecord = false,
        };

        try
        {
            using var parser = new CsvParser(reader, configuration);

            // CsvParser hands back blank lines as records, so they are skipped here, before the
            // header as well as between rows.
            bool ReadNonBlank() { while (parser.Read()) { if (!IsBlank(parser.Record!)) { return true; } } return false; }

            if (!ReadNonBlank())
            {
                Add(0, "file", "import.empty", "The file is empty.");
                return new ParseResult(rows, errors, truncated);
            }

            var columns = ResolveColumns(parser.Record!, options, Add);
            if (columns is null)
            {
                return new ParseResult(rows, errors, truncated);
            }

            var format = DateFormats[options.DateFormat];
            var row = 0;

            while (ReadNonBlank())
            {
                row++;
                if (row > MaxRows)
                {
                    Add(0, "file", "import.too_many_rows", $"A file may have at most {MaxRows:N0} rows.");
                    break;
                }

                var record = parser.Record!;
                string Field(int? index) => index is { } i && i < record.Length ? record[i] : "";

                var date = ParseDate(Field(columns.Date), format, row, Add);
                var description = ParseDescription(Field(columns.Description), row, Add);
                var amount = columns.Amount is not null
                    ? ParseAmount(Field(columns.Amount), row, Add)
                    : ParseSplitAmount(Field(columns.Debit), Field(columns.Credit), row, Add);

                if (date is not null && description is not null && amount is not null)
                {
                    rows.Add(new ParsedRow(row, date.Value, description, amount.Value));
                }

                if (truncated)
                {
                    break;
                }

            }

            if (row == 0 && errors.Count == 0)
            {
                Add(0, "file", "import.empty", "The file has a header but no rows.");
            }
        }
        catch (CsvHelperException exception)
        {
            // Unbalanced quotes and the like: the rest of the file cannot be trusted.
            Add(0, "file", "csv.malformed", $"The file is not valid CSV: {exception.Message.Split('\n')[0]}");
        }

        return new ParseResult(rows, errors, truncated);
    }

    public static ParseResult Parse(string csv, ImportOptions options) => Parse(new StringReader(csv), options);

    private static Columns? ResolveColumns(string[] header, ImportOptions options, Action<int, string, string, string> add)
    {
        var names = header.Select(h => h.Trim().TrimStart('﻿')).ToArray();
        var ok = true;

        int? Find(string? explicitName, string[] synonyms, string field, string description, bool required = true)
        {
            if (explicitName is not null)
            {
                var found = Array.FindIndex(names, n => n.Equals(explicitName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    return found;
                }

                add(0, field, "header.missing", $"The file has no column named \"{explicitName}\".");
                ok = false;
                return null;
            }

            foreach (var synonym in synonyms)
            {
                var found = Array.FindIndex(names, n => n.Equals(synonym, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                {
                    return found;
                }
            }

            if (required)
            {
                add(0, field, "header.missing", $"No {description} column found. Expected one named {string.Join(", ", synonyms.Select(s => $"\"{s}\""))}, or say which one to use.");
                ok = false;
            }

            return null;
        }

        var date = Find(options.DateColumn, DateNames, "date", "date");
        var description = Find(options.DescriptionColumn, DescriptionNames, "description", "description");

        int? amount = null;
        int? debit = null;
        int? credit = null;

        if (options.AmountColumn is not null)
        {
            // Named explicitly: a missing column is reported by Find, and there is no fallback.
            amount = Find(options.AmountColumn, AmountNames, "amount", "amount");
        }
        else
        {
            var splitRequested = options.DebitColumn is not null || options.CreditColumn is not null;
            if (!splitRequested)
            {
                amount = Find(null, AmountNames, "amount", "amount", required: false);
            }

            if (amount is null)
            {
                debit = Find(options.DebitColumn, DebitNames, "debit", "debit", required: false);
                credit = Find(options.CreditColumn, CreditNames, "credit", "credit", required: false);

                if (debit is null || credit is null)
                {
                    // An explicitly named debit or credit column that is missing was already reported.
                    var alreadyReported = (options.DebitColumn is not null && debit is null)
                        || (options.CreditColumn is not null && credit is null);
                    if (!alreadyReported)
                    {
                        add(0, "amount", "header.missing",
                            "No amount column found. Expected one named \"Amount\", or both a debit and a credit column.");
                    }

                    ok = false;
                }
            }
        }

        return ok ? new Columns(date!.Value, description!.Value, amount, debit, credit) : null;
    }

    private static DateOnly? ParseDate(string text, string format, int row, Action<int, string, string, string> add)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            add(row, "date", "date.required", "Date is missing.");
            return null;
        }

        if (!DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            add(row, "date", "date.invalid", $"\"{Shorten(value)}\" is not a date in the expected format ({format}).");
            return null;
        }

        return date;
    }

    private static string? ParseDescription(string text, int row, Action<int, string, string, string> add)
    {
        // Runs of whitespace (including a newline inside a quoted field) become one space.
        var value = Whitespace().Replace(text.Trim(), " ");

        if (value.Length == 0)
        {
            add(row, "description", "description.required", "Description is missing.");
            return null;
        }

        if (value.EnumerateRunes().Count() > DescriptionMaxLength)
        {
            add(row, "description", "description.too_long", $"Description is longer than {DescriptionMaxLength} characters.");
            return null;
        }

        if (value.Any(char.IsControl))
        {
            add(row, "description", "description.invalid", "Description contains control characters.");
            return null;
        }

        return value;
    }

    private enum NumberStatus { Ok, Blank, Invalid, OutOfRange }

    /// <summary>
    /// Accepts "12.50", "-12.50", "+12.50", "$12.50", "(12.50)" for a negative, and thousands
    /// grouping like "1,234.56". Rejects everything else on purpose: "12,50" is a European
    /// decimal comma and also a thousands typo, and guessing wrong silently changes an amount.
    /// </summary>
    private static (NumberStatus Status, decimal Value) ParseNumber(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return (NumberStatus.Blank, 0m);
        }

        var parenthesized = text.StartsWith('(') && text.EndsWith(')');
        if (parenthesized)
        {
            text = text[1..^1];
        }

        text = text.Replace("$", "").Replace("€", "").Replace("£", "").Trim();

        if (GroupedNumber().IsMatch(text))
        {
            text = text.Replace(",", "");
        }
        else if (!PlainNumber().IsMatch(text))
        {
            return (NumberStatus.Invalid, 0m);
        }

        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
        {
            return (NumberStatus.OutOfRange, 0m);
        }

        if (parenthesized)
        {
            // "(-5)" says negative twice; refuse rather than pick one.
            return value < 0 ? (NumberStatus.Invalid, 0m) : (NumberStatus.Ok, -value);
        }

        return (NumberStatus.Ok, value);
    }

    private static decimal? ParseAmount(string text, int row, Action<int, string, string, string> add)
    {
        var (status, value) = ParseNumber(text);
        switch (status)
        {
            case NumberStatus.Blank:
                add(row, "amount", "amount.required", "Amount is missing.");
                return null;
            case NumberStatus.Invalid:
                add(row, "amount", "amount.invalid", $"\"{Shorten(text.Trim())}\" is not an amount.");
                return null;
            case NumberStatus.OutOfRange:
                add(row, "amount", "amount.out_of_range", "Amount is too large.");
                return null;
        }

        if (MoneyRules.Check(value) is var (code, message))
        {
            add(row, "amount", code, message);
            return null;
        }

        return value;
    }

    /// <summary>Money in minus money out, each column read as a magnitude. Exactly one may be filled.</summary>
    private static decimal? ParseSplitAmount(string debitText, string creditText, int row, Action<int, string, string, string> add)
    {
        var (debitStatus, debit) = ParseNumber(debitText);
        var (creditStatus, credit) = ParseNumber(creditText);

        foreach (var (status, text) in new[] { (debitStatus, debitText), (creditStatus, creditText) })
        {
            if (status == NumberStatus.Invalid)
            {
                add(row, "amount", "amount.invalid", $"\"{Shorten(text.Trim())}\" is not an amount.");
                return null;
            }

            if (status == NumberStatus.OutOfRange)
            {
                add(row, "amount", "amount.out_of_range", "Amount is too large.");
                return null;
            }
        }

        var hasDebit = debitStatus == NumberStatus.Ok && debit != 0m;
        var hasCredit = creditStatus == NumberStatus.Ok && credit != 0m;

        if (hasDebit && hasCredit)
        {
            add(row, "amount", "amount.ambiguous", "Both the debit and the credit column have an amount.");
            return null;
        }

        if (!hasDebit && !hasCredit)
        {
            var blank = debitStatus == NumberStatus.Blank && creditStatus == NumberStatus.Blank;
            add(row, "amount", blank ? "amount.required" : "amount.zero",
                blank ? "Both the debit and the credit column are empty." : "Amount must not be zero.");
            return null;
        }

        var amount = hasCredit ? Math.Abs(credit) : -Math.Abs(debit);
        if (MoneyRules.Check(amount) is var (code, message))
        {
            add(row, "amount", code, message);
            return null;
        }

        return amount;
    }

    private static bool IsBlank(string[] record) => record.All(string.IsNullOrWhiteSpace);

    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "...";
}
