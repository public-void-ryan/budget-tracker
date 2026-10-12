using System.Text;
using BudgetTracker.Api.Imports;

namespace BudgetTracker.Api.Tests.Imports;

public class ImportDedupTests
{
    private static ParsedRow Row(string date, decimal amount, string description, int row = 1) =>
        new(row, DateOnly.ParseExact(date, "yyyy-MM-dd"), description, amount);

    private static string HashOf(string date, decimal amount, string description) =>
        ImportDedup.Hash(Row(date, amount, description));

    private static IReadOnlySet<(string, short)> Stored(IEnumerable<HashedRow> rows) =>
        rows.Select(r => (r.Hash, r.Sequence)).ToHashSet();

    // The hash

    [Fact]
    public void The_hash_is_sixty_four_lowercase_hex_characters_to_fit_the_column()
    {
        var hash = HashOf("2026-10-01", -4.50m, "Coffee");

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Theory]
    [InlineData("2026-10-01", -4.50, "Coffee Shop #12", "2026-10-01|-4.5000|coffee shop #12", "fdb81b664b41457a07706c662c84decd3f2cd81cca13e986446c2ec3f92950d5")]
    [InlineData("2026-10-01", -4.50, "café", "2026-10-01|-4.5000|café", "e64b1b19d822a5aa9ed9e6b2ac897fe45e4bcb6cd550c17d9317559f9452a47c")]
    [InlineData("2026-12-31", 1234.56, "Salary | Acme", "2026-12-31|1234.5600|salary | acme", "f2dd155d52272059feed6088892fca808448e98a34cdeb54d8009166bc1b1988")]
    public void The_algorithm_is_pinned_by_hashes_computed_independently(
        string date, double amount, string description, string canonical, string expectedHash)
    {
        // The expected hashes were produced with Python's hashlib, not with this code. If these
        // fail, a change to how rows are identified would silently stop re-imports from matching
        // transactions that are already stored: change them only deliberately, with a migration plan.
        Assert.Equal(canonical, ImportDedup.Canonical(DateOnly.ParseExact(date, "yyyy-MM-dd"), (decimal)amount, description));
        Assert.Equal(expectedHash, HashOf(date, (decimal)amount, description));
    }

    [Fact]
    public void The_same_row_always_hashes_the_same()
    {
        Assert.Equal(HashOf("2026-10-01", -4.50m, "Coffee"), HashOf("2026-10-01", -4.50m, "Coffee"));
    }

    [Fact]
    public void The_row_number_and_neighbouring_rows_do_not_affect_the_hash()
    {
        var first = ImportDedup.Hash(Row("2026-10-01", -4.50m, "Coffee", row: 1));
        var later = ImportDedup.Hash(Row("2026-10-01", -4.50m, "Coffee", row: 9_999));

        Assert.Equal(first, later);
    }

    [Fact]
    public void Case_and_surrounding_space_in_the_description_are_ignored()
    {
        Assert.Equal(
            HashOf("2026-10-01", -4.50m, "Starbucks #123"),
            HashOf("2026-10-01", -4.50m, "  STARBUCKS #123  "));
    }

    [Fact]
    public void Composed_and_decomposed_accents_are_the_same_description()
    {
        // The same word typed two ways: one code point for the accent, or a base letter plus a combining mark.
        Assert.Equal(
            HashOf("2026-10-01", -4.50m, "café"),
            HashOf("2026-10-01", -4.50m, "café"));
    }

    [Theory]
    [InlineData("12.5", "12.50")]
    [InlineData("12.5", "12.5000")]
    [InlineData("-12.5", "-12.50000")]
    public void The_same_amount_written_with_different_trailing_zeros_is_one_amount(string a, string b)
    {
        Assert.Equal(
            HashOf("2026-10-01", decimal.Parse(a), "x"),
            HashOf("2026-10-01", decimal.Parse(b), "x"));
    }

    [Theory]
    [InlineData("2026-10-02", -4.50, "Coffee")]   // other date
    [InlineData("2026-10-01", -4.51, "Coffee")]   // other amount
    [InlineData("2026-10-01", 4.50, "Coffee")]    // opposite sign
    [InlineData("2026-10-01", -4.50, "Coffee 2")] // other description
    [InlineData("2026-10-01", -4.50, "Cof fee")]  // a space is not ignorable inside the text
    public void Any_real_difference_changes_the_hash(string date, double amount, string description)
    {
        Assert.NotEqual(HashOf("2026-10-01", -4.50m, "Coffee"), HashOf(date, (decimal)amount, description));
    }

    [Fact]
    public void A_description_containing_the_separator_cannot_be_mistaken_for_other_fields()
    {
        // If the description were not last, "2026-10-01|-4.5|x" could collide across fields.
        Assert.NotEqual(
            HashOf("2026-10-01", -4.50m, "5000|x"),
            HashOf("2026-10-01", -4.5m, "x"));
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_machines_culture()
    {
        // Plain ASCII "I" is the classic trap: Turkish lowercases it to a dotless "\u0131", so a
        // culture-sensitive ToLower() would give the same bank text two different hashes on a
        // machine set to Turkish, and a re-import there would find nothing it had seen before.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var german = HashOf("2026-10-01", -1234.56m, "ISTANBUL AIRPORT TITLE");
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var turkish = HashOf("2026-10-01", -1234.56m, "ISTANBUL AIRPORT TITLE");

            Assert.Equal(german, turkish);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // Occurrence numbering

    [Fact]
    public void Identical_rows_in_one_file_get_consecutive_sequence_numbers()
    {
        var numbered = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "Coffee", 1),
            Row("2026-10-01", -4.50m, "Coffee", 2),
            Row("2026-10-01", -4.50m, "Coffee", 3),
        ]);

        Assert.Equal<short>([0, 1, 2], numbered.Select(r => r.Sequence).ToArray());
        Assert.Single(numbered.Select(r => r.Hash).Distinct());
    }

    [Fact]
    public void Distinct_rows_all_start_at_zero_and_keep_their_order()
    {
        var numbered = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "Coffee", 1),
            Row("2026-10-02", -3.00m, "Tea", 2),
            Row("2026-10-03", 2500m, "Salary", 3),
        ]);

        Assert.Equal<short>([0, 0, 0], numbered.Select(r => r.Sequence).ToArray());
        Assert.Equal([1, 2, 3], numbered.Select(r => r.Row.Row).ToArray());
    }

    [Fact]
    public void Identical_rows_are_counted_even_when_other_rows_sit_between_them()
    {
        var numbered = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "Coffee", 1),
            Row("2026-10-01", -3.00m, "Tea", 2),
            Row("2026-10-01", -4.50m, "Coffee", 3),
            Row("2026-10-01", -3.00m, "Tea", 4),
            Row("2026-10-01", -4.50m, "Coffee", 5),
        ]);

        Assert.Equal<short>([0, 0, 1, 1, 2], numbered.Select(r => r.Sequence).ToArray());
    }

    [Fact]
    public void Rows_that_differ_only_in_case_are_the_same_row_and_are_numbered_together()
    {
        var numbered = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "COFFEE", 1),
            Row("2026-10-01", -4.50m, "Coffee", 2),
        ]);

        Assert.Equal<short>([0, 1], numbered.Select(r => r.Sequence).ToArray());
    }

    [Fact]
    public void An_empty_file_numbers_to_nothing()
    {
        Assert.Empty(ImportDedup.Number([]));
    }

    // Classification against what is already stored

    private static IReadOnlyList<RowStatus> Statuses(IReadOnlyList<HashedRow> file, IReadOnlySet<(string, short)> stored) =>
        [.. ImportDedup.Classify(file, stored).Select(c => c.Status)];

    [Fact]
    public void Nothing_stored_means_everything_is_new()
    {
        var file = ImportDedup.Number([Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-02", -3m, "Tea")]);

        Assert.Equal([RowStatus.New, RowStatus.New], Statuses(file, new HashSet<(string, short)>()));
    }

    [Fact]
    public void Re_importing_the_same_file_finds_every_row_already_there()
    {
        var file = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-02", -3m, "Tea"),
        ]);

        Assert.Equal([RowStatus.Duplicate, RowStatus.Duplicate, RowStatus.Duplicate], Statuses(file, Stored(file)));
    }

    [Fact]
    public void A_later_export_with_a_genuine_extra_coffee_adds_only_the_extra_one()
    {
        // Day 1 export showed two coffees; a later export of the same day shows three.
        var earlier = ImportDedup.Number([Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-01", -4.50m, "Coffee")]);
        var later = ImportDedup.Number(
        [
            Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-01", -4.50m, "Coffee"),
        ]);

        Assert.Equal(
            [RowStatus.Duplicate, RowStatus.Duplicate, RowStatus.New],
            Statuses(later, Stored(earlier)));
    }

    [Fact]
    public void A_later_export_with_fewer_of_a_repeated_row_adds_nothing()
    {
        var earlier = ImportDedup.Number([Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-01", -4.50m, "Coffee")]);
        var later = ImportDedup.Number([Row("2026-10-01", -4.50m, "Coffee")]);

        Assert.Equal([RowStatus.Duplicate], Statuses(later, Stored(earlier)));
    }

    [Fact]
    public void Overlapping_statements_import_only_the_rows_the_other_did_not_have()
    {
        var january = ImportDedup.Number(
        [
            Row("2026-01-30", -10m, "A"), Row("2026-01-31", -20m, "B"), Row("2026-02-01", -30m, "C"),
        ]);
        var lateJanuaryToFebruary = ImportDedup.Number(
        [
            Row("2026-01-31", -20m, "B"), Row("2026-02-01", -30m, "C"), Row("2026-02-02", -40m, "D"),
        ]);

        Assert.Equal(
            [RowStatus.Duplicate, RowStatus.Duplicate, RowStatus.New],
            Statuses(lateJanuaryToFebruary, Stored(january)));
    }

    [Fact]
    public void A_row_deleted_after_import_comes_back_when_the_file_is_imported_again()
    {
        // Only what is stored counts as "already imported", so a transaction the user deleted
        // is treated as new when its file is imported again. Documented behaviour, not an accident.
        var file = ImportDedup.Number([Row("2026-10-01", -4.50m, "Coffee"), Row("2026-10-02", -3m, "Tea")]);
        var storedAfterDeletingTheCoffee = new HashSet<(string, short)> { (file[1].Hash, 0) };

        Assert.Equal([RowStatus.New, RowStatus.Duplicate], Statuses(file, storedAfterDeletingTheCoffee));
    }

    [Fact]
    public void Classification_keeps_the_files_order_and_the_original_rows()
    {
        var file = ImportDedup.Number(
        [
            Row("2026-10-03", -1m, "C", 3), Row("2026-10-01", -2m, "A", 1), Row("2026-10-02", -3m, "B", 2),
        ]);

        var classified = ImportDedup.Classify(file, new HashSet<(string, short)>());

        Assert.Equal(["C", "A", "B"], classified.Select(c => c.Hashed.Row.Description).ToArray());
    }

    [Fact]
    public void Identical_rows_in_the_same_file_are_never_collapsed_into_one()
    {
        var file = ImportDedup.Number(Enumerable.Range(0, 5).Select(i => Row("2026-10-01", -4.50m, "Coffee", i + 1)));

        var classified = ImportDedup.Classify(file, new HashSet<(string, short)>());

        Assert.Equal(5, classified.Count(c => c.Status == RowStatus.New));
        Assert.Equal(5, classified.Select(c => (c.Hashed.Hash, c.Hashed.Sequence)).Distinct().Count());
    }

    // Parser and dedup together, end to end without a database

    [Fact]
    public void Two_exports_of_the_same_account_with_different_formatting_deduplicate_against_each_other()
    {
        var firstExport = CsvTransactionParser.Parse(
            "Date,Description,Amount\n2026-10-01,STARBUCKS #12,-4.5\n2026-10-01,Starbucks #12,-4.50\n2026-10-02,Rent,\"-1,000.00\"\n",
            new ImportOptions());
        var secondExport = CsvTransactionParser.Parse(
            "Posted Date,Memo,Amount\n2026-10-01,  starbucks   #12 ,($4.50)\n2026-10-01,STARBUCKS #12,-4.50\n2026-10-01,Starbucks #12,-4.5\n2026-10-02,RENT,-1000\n",
            new ImportOptions());

        Assert.True(firstExport.Succeeded);
        Assert.True(secondExport.Succeeded);
        var stored = Stored(ImportDedup.Number(firstExport.Rows));

        var statuses = Statuses(ImportDedup.Number(secondExport.Rows), stored);

        // Three coffees against two stored: the third is genuinely new. Rent matches despite
        // different case, grouping commas and trailing zeros.
        Assert.Equal([RowStatus.Duplicate, RowStatus.Duplicate, RowStatus.New, RowStatus.Duplicate], statuses);
    }

    [Fact]
    public void The_sequence_always_fits_the_smallint_column_for_the_largest_allowed_file()
    {
        var rows = Enumerable.Range(0, CsvTransactionParser.MaxRows).Select(i => Row("2026-10-01", -1m, "Same row", i + 1));

        var numbered = ImportDedup.Number(rows);

        Assert.Equal((short)(CsvTransactionParser.MaxRows - 1), numbered[^1].Sequence);
        Assert.True(numbered[^1].Sequence <= short.MaxValue);
    }
}
