using System.Text;
using BudgetTracker.Api.Imports;

namespace BudgetTracker.Api.Tests.Imports;

public class CsvTransactionParserTests
{
    private static ParseResult Parse(string csv, ImportOptions? options = null) =>
        CsvTransactionParser.Parse(csv, options ?? new ImportOptions());

    private static (int Row, string Field, string Code)[] Problems(ParseResult result) =>
        [.. result.Errors.Select(e => (e.Row, e.Field, e.Code))];

    private static ParsedRow Only(ParseResult result)
    {
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => $"{e.Row}:{e.Field}:{e.Code}")));
        return Assert.Single(result.Rows);
    }

    // The happy path

    [Fact]
    public void A_simple_file_is_parsed_into_rows()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,Coffee,-4.50\n2026-10-02,Salary,2500.00\n");

        Assert.True(result.Succeeded);
        Assert.Equal(
            [(1, new DateOnly(2026, 10, 1), "Coffee", -4.50m), (2, new DateOnly(2026, 10, 2), "Salary", 2500.00m)],
            result.Rows.Select(r => (r.Row, r.Date, r.Description, r.Amount)).ToArray());
    }

    [Fact]
    public void Row_numbers_count_data_rows_and_ignore_the_header()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,A,1\n2026-10-02,B,xx\n");

        Assert.Equal([(2, "amount", "amount.invalid")], Problems(result));
    }

    [Fact]
    public void Windows_line_endings_a_byte_order_mark_and_a_missing_final_newline_are_fine()
    {
        var csv = "﻿Date,Description,Amount\r\n2026-10-01,Coffee,-4.50\r\n2026-10-02,Tea,-3.00";

        var result = Parse(csv);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void A_byte_order_mark_read_from_a_real_stream_does_not_break_the_first_header()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-10-01,Coffee,-4.50\n")).ToArray();

        var result = CsvTransactionParser.Parse(new StreamReader(new MemoryStream(bytes)), new ImportOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Blank_lines_are_skipped_and_do_not_shift_row_numbers()
    {
        var result = Parse("Date,Description,Amount\n\n2026-10-01,A,1\n\n,,\n2026-10-02,B,xx\n");

        Assert.Equal(2, result.Rows.Count + result.Errors.Count);
        Assert.Equal([(2, "amount", "amount.invalid")], Problems(result));
    }

    [Fact]
    public void Extra_columns_are_ignored()
    {
        var result = Parse("Date,Check #,Description,Balance,Amount\n2026-10-01,1001,Rent,1000.00,-900.00\n");

        var row = Only(result);
        Assert.Equal(("Rent", -900.00m), (row.Description, row.Amount));
    }

    // Quoting

    [Fact]
    public void Quoted_fields_may_contain_commas_quotes_and_newlines()
    {
        var csv = "Date,Description,Amount\n2026-10-01,\"Smith, J \"\"Jay\"\"\nAcme Ltd\",-4.50\n";

        var row = Only(Parse(csv));

        Assert.Equal("Smith, J \"Jay\" Acme Ltd", row.Description);
    }

    [Fact]
    public void Unbalanced_quotes_are_reported_as_a_malformed_file_not_thrown()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,\"never closed,-4.50\n2026-10-02,B,-1\n");

        Assert.Contains(result.Errors, e => e.Code == "csv.malformed");
    }

    // Header detection

    [Theory]
    [InlineData("Date,Description,Amount")]
    [InlineData("date,description,amount")]
    [InlineData("  DATE , Description ,AMOUNT")]
    [InlineData("Posted Date,Memo,Amount")]
    [InlineData("Transaction Date,Payee,Amount")]
    [InlineData("Posting Date,Details,Amount")]
    [InlineData("Trans Date,Narrative,Amount")]
    public void Common_header_names_are_detected_ignoring_case_and_padding(string header)
    {
        var row = Only(Parse($"{header}\n2026-10-01,Coffee,-4.50\n"));

        Assert.Equal(("Coffee", -4.50m), (row.Description, row.Amount));
    }

    [Fact]
    public void Explicit_column_names_override_detection()
    {
        var csv = "Date,Posted Date,Memo,Description,Amount\n2026-01-01,2026-10-01,from memo,from description,-4.50\n";

        var detected = Only(Parse(csv));
        var explicitly = Only(Parse(csv, new ImportOptions(DateColumn: "Posted Date", DescriptionColumn: "Memo")));

        Assert.Equal((new DateOnly(2026, 1, 1), "from description"), (detected.Date, detected.Description));
        Assert.Equal((new DateOnly(2026, 10, 1), "from memo"), (explicitly.Date, explicitly.Description));
    }

    [Fact]
    public void An_explicit_column_that_does_not_exist_is_an_error_naming_it()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,A,1\n", new ImportOptions(AmountColumn: "Total"));

        var error = Assert.Single(result.Errors);
        Assert.Equal((0, "amount", "header.missing"), (error.Row, error.Field, error.Code));
        Assert.Contains("Total", error.Message);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Every_missing_header_is_reported_at_once()
    {
        var result = Parse("Foo,Bar,Baz\n1,2,3\n");

        Assert.Equal(
            [(0, "date", "header.missing"), (0, "description", "header.missing"), (0, "amount", "header.missing")],
            Problems(result));
    }

    // Dates

    [Theory]
    [InlineData(DateFormatName.Iso, "2026-10-31", 2026, 10, 31)]
    [InlineData(DateFormatName.Us, "10/31/2026", 2026, 10, 31)]
    [InlineData(DateFormatName.Us, "1/5/2026", 2026, 1, 5)]
    [InlineData(DateFormatName.Eu, "31/10/2026", 2026, 10, 31)]
    [InlineData(DateFormatName.Eu, "5/1/2026", 2026, 1, 5)]
    [InlineData(DateFormatName.IsoSlash, "2026/10/31", 2026, 10, 31)]
    [InlineData(DateFormatName.EuDots, "31.10.2026", 2026, 10, 31)]
    public void Each_named_date_format_is_understood(DateFormatName format, string text, int year, int month, int day)
    {
        var row = Only(Parse($"Date,Description,Amount\n{text},A,1\n", new ImportOptions(DateFormat: format)));

        Assert.Equal(new DateOnly(year, month, day), row.Date);
    }

    [Fact]
    public void The_same_text_means_different_dates_in_us_and_eu_layouts()
    {
        // 03/04/2026 is 4 March in the US and 3 April in Europe, which is why the layout
        // is an explicit choice rather than a guess.
        var us = Only(Parse("Date,Description,Amount\n03/04/2026,A,1\n", new ImportOptions(DateFormat: DateFormatName.Us)));
        var eu = Only(Parse("Date,Description,Amount\n03/04/2026,A,1\n", new ImportOptions(DateFormat: DateFormatName.Eu)));

        Assert.Equal(new DateOnly(2026, 3, 4), us.Date);
        Assert.Equal(new DateOnly(2026, 4, 3), eu.Date);
    }

    [Theory]
    [InlineData("", "date.required")]
    [InlineData("   ", "date.required")]
    [InlineData("10/31/2026", "date.invalid")]       // wrong layout for the default (ISO)
    [InlineData("2026-13-01", "date.invalid")]
    [InlineData("2026-02-30", "date.invalid")]
    [InlineData("2026-10-01T10:00:00", "date.invalid")]
    [InlineData("yesterday", "date.invalid")]
    public void A_bad_date_is_reported_for_its_row(string text, string code)
    {
        var result = Parse($"Date,Description,Amount\n{text},A,1\n");

        Assert.Equal([(1, "date", code)], Problems(result));
    }

    // Amounts

    [Theory]
    [InlineData("4.50", 4.50)]
    [InlineData("-4.50", -4.50)]
    [InlineData("+4.50", 4.50)]
    [InlineData("$4.50", 4.50)]
    [InlineData("-$4.50", -4.50)]
    [InlineData("$-4.50", -4.50)]
    [InlineData("€4.50", 4.50)]
    [InlineData("£4.50", 4.50)]
    [InlineData("(4.50)", -4.50)]
    [InlineData("($4.50)", -4.50)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-1,234,567.89", -1234567.89)]
    [InlineData("  4.50  ", 4.50)]
    [InlineData("4", 4)]
    [InlineData("0.0001", 0.0001)]
    [InlineData("12.50000", 12.5)]
    public void Common_ways_of_writing_an_amount_are_understood(string text, double expected)
    {
        var row = Only(Parse($"Date,Description,Amount\n2026-10-01,A,\"{text}\"\n"));

        Assert.Equal((decimal)expected, row.Amount);
    }

    [Theory]
    [InlineData("12,50", "amount.invalid")]       // a European decimal comma and a typo look the same
    [InlineData("1,23", "amount.invalid")]
    [InlineData("1,2345.00", "amount.invalid")]   // thousands groups must be three digits
    [InlineData("abc", "amount.invalid")]
    [InlineData("12.5.0", "amount.invalid")]
    [InlineData("--4", "amount.invalid")]
    [InlineData("(-4.50)", "amount.invalid")]     // negative twice
    [InlineData("4.50-", "amount.invalid")]
    [InlineData("4 USD", "amount.invalid")]
    [InlineData("1e3", "amount.invalid")]
    [InlineData("", "amount.required")]
    [InlineData("0", "amount.zero")]
    [InlineData("0.00", "amount.zero")]
    [InlineData("12.34567", "amount.too_precise")]
    [InlineData("1000000000000000", "amount.out_of_range")]
    [InlineData("99999999999999999999999999999999", "amount.out_of_range")]
    public void An_unusable_amount_is_reported_with_its_reason(string text, string code)
    {
        var result = Parse($"Date,Description,Amount\n2026-10-01,A,\"{text}\"\n");

        Assert.Equal([(1, "amount", code)], Problems(result));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void The_largest_valid_amount_is_accepted_and_one_cent_more_is_not()
    {
        Assert.Equal(999_999_999_999_999.9999m, Only(Parse("Date,Description,Amount\n2026-10-01,A,999999999999999.9999\n")).Amount);
        Assert.Equal(
            "amount.out_of_range",
            Parse("Date,Description,Amount\n2026-10-01,A,1000000000000000.0000\n").Errors.Single().Code);
    }

    // Debit and credit columns

    private const string Split = "Date,Description,Debit,Credit\n";

    [Fact]
    public void Separate_debit_and_credit_columns_become_one_signed_amount()
    {
        var result = Parse(Split + "2026-10-01,Coffee,4.50,\n2026-10-02,Salary,,2500.00\n");

        Assert.True(result.Succeeded);
        Assert.Equal([-4.50m, 2500.00m], result.Rows.Select(r => r.Amount).ToArray());
    }

    [Fact]
    public void Debit_and_credit_are_read_as_magnitudes_whatever_their_sign()
    {
        var result = Parse(Split + "2026-10-01,A,-4.50,\n2026-10-02,B,,-7.00\n2026-10-03,C,(3.00),\n");

        Assert.Equal([-4.50m, 7.00m, -3.00m], result.Rows.Select(r => r.Amount).ToArray());
    }

    [Theory]
    [InlineData("4.50,3.00", "amount.ambiguous")]
    [InlineData(",", "amount.required")]
    [InlineData("0,", "amount.zero")]
    [InlineData("0.00,0.00", "amount.zero")]
    [InlineData("abc,", "amount.invalid")]
    [InlineData(",4.5.0", "amount.invalid")]
    public void A_row_must_fill_exactly_one_of_debit_and_credit(string cells, string code)
    {
        var result = Parse(Split + $"2026-10-01,A,{cells}\n");

        Assert.Equal([(1, "amount", code)], Problems(result));
    }

    [Fact]
    public void A_file_with_a_debit_column_but_no_credit_column_has_no_usable_amount()
    {
        var result = Parse("Date,Description,Debit\n2026-10-01,A,4.50\n");

        Assert.Equal([(0, "amount", "header.missing")], Problems(result));
    }

    [Fact]
    public void An_amount_column_wins_when_the_file_has_both_kinds()
    {
        var row = Only(Parse("Date,Description,Amount,Debit,Credit\n2026-10-01,A,-9.99,1.00,2.00\n"));

        Assert.Equal(-9.99m, row.Amount);
    }

    [Fact]
    public void Explicit_debit_and_credit_columns_are_used_even_when_an_amount_column_exists()
    {
        var row = Only(Parse(
            "Date,Description,Amount,Out,In\n2026-10-01,A,-9.99,1.00,\n",
            new ImportOptions(DebitColumn: "Out", CreditColumn: "In")));

        Assert.Equal(-1.00m, row.Amount);
    }

    // Descriptions

    [Fact]
    public void Descriptions_are_trimmed_and_runs_of_whitespace_collapse()
    {
        var row = Only(Parse("Date,Description,Amount\n2026-10-01,\"  CARD   PURCHASE\t\tCOFFEE \",-4\n"));

        Assert.Equal("CARD PURCHASE COFFEE", row.Description);
    }

    [Theory]
    [InlineData("", "description.required")]
    [InlineData("   ", "description.required")]
    public void A_blank_description_is_reported(string text, string code)
    {
        var result = Parse($"Date,Description,Amount\n2026-10-01,\"{text}\",-4\n");

        Assert.Equal([(1, "description", code)], Problems(result));
    }

    [Fact]
    public void A_description_may_be_five_hundred_characters_and_not_one_more()
    {
        Assert.True(Parse($"Date,Description,Amount\n2026-10-01,{new string('d', 500)},-4\n").Succeeded);
        Assert.Equal(
            [(1, "description", "description.too_long")],
            Problems(Parse($"Date,Description,Amount\n2026-10-01,{new string('d', 501)},-4\n")));
    }

    [Fact]
    public void Description_length_counts_characters_not_utf16_units()
    {
        var hundredEmoji = string.Concat(Enumerable.Repeat("😀", 500));

        Assert.True(Parse($"Date,Description,Amount\n2026-10-01,{hundredEmoji},-4\n").Succeeded);
    }

    [Fact]
    public void A_control_character_in_a_description_is_reported()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,bad\u0000value,-4\n");

        Assert.Equal([(1, "description", "description.invalid")], Problems(result));
    }

    // Whole-file behaviour

    [Fact]
    public void Every_problem_in_every_row_is_reported_not_just_the_first()
    {
        var result = Parse("Date,Description,Amount\nbad,,xx\n2026-10-02,OK,1\n2026-10-03,,0\n");

        Assert.Equal(
            [
                (1, "date", "date.invalid"), (1, "description", "description.required"), (1, "amount", "amount.invalid"),
                (3, "description", "description.required"), (3, "amount", "amount.zero"),
            ],
            Problems(result));
        Assert.Single(result.Rows);
    }

    [Fact]
    public void A_short_row_is_missing_its_trailing_fields_not_a_crash()
    {
        var result = Parse("Date,Description,Amount\n2026-10-01,Coffee\n");

        Assert.Equal([(1, "amount", "amount.required")], Problems(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public void An_empty_file_is_reported(string csv)
    {
        var error = Assert.Single(Parse(csv).Errors);

        Assert.Equal("import.empty", error.Code);
    }

    [Fact]
    public void A_header_with_no_rows_is_reported()
    {
        var error = Assert.Single(Parse("Date,Description,Amount\n").Errors);

        Assert.Equal("import.empty", error.Code);
    }

    [Fact]
    public void Reported_errors_are_capped_and_the_result_says_so()
    {
        var rows = string.Concat(Enumerable.Range(0, 500).Select(i => "bad,x,1\n"));

        var result = Parse("Date,Description,Amount\n" + rows);

        Assert.Equal(CsvTransactionParser.MaxReportedErrors, result.Errors.Count);
        Assert.True(result.ErrorsTruncated);
    }

    [Fact]
    public void A_file_just_under_the_error_cap_is_not_marked_truncated()
    {
        var rows = string.Concat(Enumerable.Range(0, 99).Select(_ => "bad,x,1\n"));

        var result = Parse("Date,Description,Amount\n" + rows);

        Assert.Equal(99, result.Errors.Count);
        Assert.False(result.ErrorsTruncated);
    }

    [Fact]
    public void Ten_thousand_rows_are_accepted_and_one_more_is_refused()
    {
        string File(int count) => "Date,Description,Amount\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"2026-10-01,Row {i},-1\n"));

        var atTheLimit = Parse(File(CsvTransactionParser.MaxRows));
        var overTheLimit = Parse(File(CsvTransactionParser.MaxRows + 1));

        Assert.True(atTheLimit.Succeeded);
        Assert.Equal(CsvTransactionParser.MaxRows, atTheLimit.Rows.Count);
        Assert.Equal("import.too_many_rows", overTheLimit.Errors.Single().Code);
    }

    [Fact]
    public void Arbitrary_bytes_never_throw()
    {
        var random = new Random(2026);
        var alphabet = "abc,\"\n\r\t 0123456789-.()$€日🔑\u0000﻿";

        for (var i = 0; i < 500; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            var exception = Record.Exception(() => Parse(text));

            Assert.Null(exception);
        }
    }
}
