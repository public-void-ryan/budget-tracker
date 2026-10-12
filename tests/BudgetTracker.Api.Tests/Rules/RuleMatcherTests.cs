using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Rules;

namespace BudgetTracker.Api.Tests.Rules;

public class RuleMatcherTests
{
    private static readonly Guid Groceries = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Dining = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static RuleDefinition Rule(
        string pattern, RuleMatchType type = RuleMatchType.Contains, int position = 0,
        Guid? category = null, bool transfer = false, Guid? id = null) =>
        new(id ?? Guid.CreateVersion7(), position, type, pattern, category ?? (transfer ? null : Groceries), transfer);

    private static RuleDefinition? Match(string description, params RuleDefinition[] rules) =>
        new RuleMatcher(rules).FirstMatch(description);

    // The three match types

    [Theory]
    [InlineData(RuleMatchType.Contains, "starbucks", "STARBUCKS #12 SEATTLE", true)]
    [InlineData(RuleMatchType.Contains, "starbucks", "PAYMENT TO STARBUCKS", true)]
    [InlineData(RuleMatchType.Contains, "starbucks", "STARBUCKS", true)]
    [InlineData(RuleMatchType.Contains, "starbucks", "STARBUCK", false)]
    [InlineData(RuleMatchType.StartsWith, "starbucks", "STARBUCKS #12", true)]
    [InlineData(RuleMatchType.StartsWith, "starbucks", "PAYMENT TO STARBUCKS", false)]
    [InlineData(RuleMatchType.StartsWith, "starbucks", "STARBUCKS", true)]
    [InlineData(RuleMatchType.Equals, "starbucks", "STARBUCKS", true)]
    [InlineData(RuleMatchType.Equals, "starbucks", "STARBUCKS #12", false)]
    [InlineData(RuleMatchType.Equals, "starbucks", "PAYMENT TO STARBUCKS", false)]
    public void Each_match_type_means_what_it_says(RuleMatchType type, string pattern, string description, bool expected)
    {
        Assert.Equal(expected, Match(description, Rule(pattern, type)) is not null);
    }

    // What counts as the same text

    [Theory]
    [InlineData("starbucks", "STARBUCKS #12")]
    [InlineData("STARBUCKS", "starbucks #12")]
    [InlineData("  starbucks  ", "STARBUCKS #12")]
    [InlineData("card purchase", "CARD     PURCHASE  COFFEE")]
    [InlineData("card purchase", "CARD\tPURCHASE COFFEE")]
    [InlineData("card   purchase", "CARD PURCHASE COFFEE")]
    [InlineData("card\tpurchase", "CARD PURCHASE COFFEE")]
    [InlineData("  card   purchase  ", "CARD     PURCHASE COFFEE")]
    [InlineData("café", "CAFÉ DE PARIS")]
    [InlineData("café", "CAFÉ DE PARIS")]
    public void Case_spacing_and_unicode_composition_are_ignored(string pattern, string description)
    {
        Assert.NotNull(Match(description, Rule(pattern)));
    }

    [Fact]
    public void Matching_does_not_depend_on_the_machines_culture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

            Assert.NotNull(Match("ISTANBUL AIRPORT", Rule("istanbul")));
            Assert.NotNull(Match("istanbul airport", Rule("ISTANBUL")));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(".*", "ANYTHING AT ALL", false)]
    [InlineData(".*", "WEIRD .* TEXT", true)]
    [InlineData("a|b", "A", false)]
    [InlineData("a|b", "CAR A|B CLUB", true)]
    [InlineData("[abc]", "A", false)]
    [InlineData("[abc]", "SHOP [ABC] LTD", true)]
    [InlineData("(x)", "X", false)]
    [InlineData("(x)", "PAYMENT (X)", true)]
    [InlineData("%", "50 PERCENT", false)]
    [InlineData("_", "ANYTHING", false)]
    [InlineData("\\d+", "12345", false)]
    public void Patterns_are_matched_literally_never_as_regular_expressions_or_sql_wildcards(
        string pattern, string description, bool expected)
    {
        Assert.Equal(expected, Match(description, Rule(pattern)) is not null);
    }

    [Fact]
    public void A_pattern_that_would_hang_a_regex_engine_is_just_text()
    {
        var evil = Rule("(a+)+$");

        var result = Match(new string('a', 5_000) + "!", evil);

        Assert.Null(result);
    }

    // Which rule wins

    [Fact]
    public void The_lowest_position_wins_even_when_several_rules_match()
    {
        var rules = new[]
        {
            Rule("coffee", position: 2, category: Dining),
            Rule("starbucks coffee", position: 0, category: Groceries),
            Rule("starbucks", position: 1, category: Dining),
        };

        Assert.Equal(Groceries, Match("STARBUCKS COFFEE", rules)!.CategoryId);
    }

    [Fact]
    public void The_order_rules_are_supplied_in_makes_no_difference()
    {
        var first = Rule("shop", position: 0, category: Groceries);
        var second = Rule("shop", position: 1, category: Dining);

        Assert.Equal(first.Id, Match("A SHOP", second, first)!.Id);
        Assert.Equal(first.Id, Match("A SHOP", first, second)!.Id);
    }

    [Fact]
    public void A_specific_rule_placed_first_beats_a_broad_rule_placed_later_and_not_the_reverse()
    {
        var specific = Rule("amazon prime", RuleMatchType.StartsWith, position: 0, category: Dining);
        var broad = Rule("amazon", RuleMatchType.Contains, position: 1, category: Groceries);

        Assert.Equal(Dining, Match("AMAZON PRIME VIDEO", specific, broad)!.CategoryId);
        Assert.Equal(Groceries, Match("AMAZON MARKETPLACE", specific, broad)!.CategoryId);

        var broadFirst = broad with { Position = 0 };
        var specificSecond = specific with { Position = 1 };
        Assert.Equal(Groceries, Match("AMAZON PRIME VIDEO", broadFirst, specificSecond)!.CategoryId);
    }

    [Fact]
    public void Rules_that_do_not_match_are_skipped_and_the_next_one_is_tried()
    {
        var rules = new[]
        {
            Rule("rent", position: 0, category: Dining),
            Rule("salary", position: 1, category: Groceries),
        };

        Assert.Equal(Groceries, Match("ACME SALARY", rules)!.CategoryId);
    }

    [Fact]
    public void With_nothing_matching_or_nothing_defined_there_is_no_result()
    {
        Assert.Null(Match("ANYTHING", Rule("zzz")));
        Assert.Null(new RuleMatcher([]).FirstMatch("ANYTHING"));
        Assert.Equal(0, new RuleMatcher([]).Count);
    }

    [Fact]
    public void The_result_carries_the_whole_action()
    {
        var transferOnly = Rule("payment thank you", position: 0, transfer: true);

        var hit = Match("PAYMENT THANK YOU", transferOnly)!;

        Assert.Null(hit.CategoryId);
        Assert.True(hit.MarkTransfer);
    }

    // Ties

    [Fact]
    public void Rules_at_the_same_position_are_ordered_by_id_so_an_earlier_id_wins()
    {
        // Version 7 ids start with a millisecond timestamp, so an id made earlier sorts first.
        // The timestamps are explicit and a second apart: two ids generated within the same
        // millisecond have no guaranteed order, which would make this test flaky.
        var now = DateTimeOffset.UtcNow;
        var earlier = Rule("shop", position: 5, category: Groceries, id: Guid.CreateVersion7(now));
        var later = Rule("shop", position: 5, category: Dining, id: Guid.CreateVersion7(now.AddSeconds(1)));

        Assert.Equal(earlier.Id, Match("A SHOP", later, earlier)!.Id);
        Assert.Equal(earlier.Id, Match("A SHOP", earlier, later)!.Id);
    }

    [Fact]
    public void Ties_are_broken_by_id_in_byte_order_whichever_half_of_the_range_the_ids_are_in()
    {
        // 0x7f and 0x80 straddle the signed boundary of a leading byte. The database orders ids
        // byte by byte, unsigned, so the matcher must too; the tie must not flip across it.
        var low = Guid.Parse("7fffffff-0000-0000-0000-000000000000");
        var high = Guid.Parse("80000000-0000-0000-0000-000000000000");

        var matcher = new RuleMatcher(
        [
            Rule("shop", position: 0, category: Dining, id: high),
            Rule("shop", position: 0, category: Groceries, id: low),
        ]);

        Assert.Equal(low, matcher.FirstMatch("A SHOP")!.Id);
        Assert.Equal([low, high], matcher.InOrder.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void The_runtimes_guid_order_is_the_same_as_byte_order_which_is_the_databases_order()
    {
        // The matcher leans on this. Checked rather than assumed: it does not hold everywhere
        // (SQL Server's uniqueidentifier, for one, orders differently), so a runtime change
        // that broke it should fail here instead of silently reordering rules.
        var guids = Enumerable.Range(0, 2_000).Select(_ => Guid.NewGuid()).ToList();
        guids.AddRange(new[]
        {
            "7fffffff-0000-0000-0000-000000000000", "80000000-0000-0000-0000-000000000000",
            "00000000-7fff-0000-0000-000000000000", "00000000-8000-0000-0000-000000000000",
            "00000000-0000-7fff-0000-000000000000", "00000000-0000-8000-0000-000000000000",
            "00000000-0000-0000-7f00-000000000000", "00000000-0000-0000-8000-000000000000",
            "00000000-0000-0000-0000-7f0000000000", "00000000-0000-0000-0000-800000000000",
        }.Select(Guid.Parse));

        var byRuntime = guids.Order().ToList();
        var byBytes = guids.OrderBy(g => Convert.ToHexString(g.ToByteArray(bigEndian: true)), StringComparer.Ordinal).ToList();

        Assert.Equal(byBytes, byRuntime);
    }

    // Scale

    [Fact]
    public void Five_hundred_rules_against_many_descriptions_works()
    {
        var rules = Enumerable.Range(0, 500).Select(i => Rule($"merchant {i:000}", position: i)).ToArray();
        var matcher = new RuleMatcher(rules);

        var hits = Enumerable.Range(0, 20_000)
            .Count(i => matcher.FirstMatch($"PURCHASE AT MERCHANT {i % 600:000} CITY") is not null);

        // Merchants 000 to 499 have a rule; 500 to 599 do not.
        Assert.Equal(Enumerable.Range(0, 20_000).Count(i => i % 600 < 500), hits);
    }
}
