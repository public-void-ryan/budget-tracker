using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BudgetTracker.Api.Tests.Database;

public class RuleConstraintTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    private static Rule NewRule(User user, Category? category, string pattern = "starbucks",
        RuleMatchType type = RuleMatchType.Contains, bool transfer = false, int position = 0) => new()
    {
        UserId = user.Id,
        CategoryId = category?.Id,
        Pattern = pattern,
        MatchType = type,
        MarkTransfer = transfer,
        Position = position,
    };

    private async Task<Rule> AddRuleAsync(Rule rule)
    {
        Db.Add(rule);
        await Db.SaveChangesAsync();
        return rule;
    }

    // What a rule may be

    [Fact]
    public async Task A_rule_may_assign_a_category_mark_a_transfer_or_do_both()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AddRuleAsync(NewRule(user, category, "a"));
        await AddRuleAsync(NewRule(user, category: null, "b", transfer: true));
        await AddRuleAsync(NewRule(user, category, "c", transfer: true));

        Assert.Equal(3, await Db.Rules.CountAsync());
    }

    [Fact]
    public async Task A_rule_that_does_nothing_is_refused()
    {
        var user = await AddUserAsync();

        await AssertViolationAsync(CheckViolation, "ck_rules_has_action", async () =>
        {
            Db.Add(NewRule(user, category: null, transfer: false));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Only_the_three_match_types_are_allowed()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_rules_match_type", async () =>
        {
            Db.Add(NewRule(user, category, type: (RuleMatchType)99));
            await Db.SaveChangesAsync();
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task A_blank_pattern_is_refused(string pattern)
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_rules_pattern_not_blank", async () =>
        {
            Db.Add(NewRule(user, category, pattern));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task A_negative_position_is_refused()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_rules_position_nonnegative", async () =>
        {
            Db.Add(NewRule(user, category, position: -1));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task A_pattern_may_be_two_hundred_characters_and_not_more()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);
        await AddRuleAsync(NewRule(user, category, new string('p', 200)));

        Db.Add(NewRule(user, category, new string('p', 201), position: 1));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());

        // String data right truncation: a length limit, not a named constraint.
        Assert.Equal("22001", Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Rules_with_the_same_pattern_are_allowed_and_are_told_apart_by_order()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AddRuleAsync(NewRule(user, category, "same", position: 0));
        await AddRuleAsync(NewRule(user, category, "same", position: 1));

        Assert.Equal(2, await Db.Rules.CountAsync());
    }

    // Ownership and deletion

    [Fact]
    public async Task A_rule_cannot_point_at_another_users_category()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");

        await AssertViolationAsync(ForeignKeyViolation, "fk_rules_categories_user_id_category_id", async () =>
        {
            Db.Add(NewRule(alice, bobsCategory));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Deleting_a_category_deletes_the_rules_that_assign_it_and_no_others()
    {
        var user = await AddUserAsync();
        var doomed = await AddCategoryAsync(user, "Doomed");
        var kept = await AddCategoryAsync(user, "Kept");
        await AddRuleAsync(NewRule(user, doomed, "gone"));
        await AddRuleAsync(NewRule(user, doomed, "gone too", transfer: true));
        var keptRule = await AddRuleAsync(NewRule(user, kept, "stays"));
        var transferOnly = await AddRuleAsync(NewRule(user, category: null, "transfer", transfer: true));

        await Db.Categories.Where(c => c.Id == doomed.Id).ExecuteDeleteAsync();

        var remaining = await Db.Rules.AsNoTracking().Select(r => r.Id).ToListAsync();
        Assert.Equal(new[] { keptRule.Id, transferOnly.Id }.Order().ToArray(), remaining.Order().ToArray());
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_rules_and_only_theirs()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        await AddRuleAsync(NewRule(alice, await AddCategoryAsync(alice), "a"));
        var bobsRule = await AddRuleAsync(NewRule(bob, await AddCategoryAsync(bob), "b"));

        await Db.Users.Where(u => u.Id == alice.Id).ExecuteDeleteAsync();

        Assert.Equal(bobsRule.Id, Assert.Single(await Db.Rules.AsNoTracking().ToListAsync()).Id);
    }

    // Order

    [Fact]
    public async Task The_database_and_the_matcher_agree_on_the_order_of_rules_at_the_same_position()
    {
        // If they disagreed, the list a user sees would not be the order rules actually win in.
        // Random (version 4) ids put ties on both sides of every byte boundary.
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);
        var random = new Random(31);
        Db.AddRange(Enumerable.Range(0, 300).Select(i => new Rule
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CategoryId = category.Id,
            Position = random.Next(0, 5),
            Pattern = $"p{i}",
        }));
        await Db.SaveChangesAsync();

        var databaseOrder = await Db.Rules.AsNoTracking()
            .OrderBy(r => r.Position).ThenBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync();
        var matcherOrder = new RuleMatcher((await Db.Rules.AsNoTracking().ToListAsync())
            .Select(r => new RuleDefinition(r.Id, r.Position, r.MatchType, r.Pattern, r.CategoryId, r.MarkTransfer)))
            .InOrder.Select(r => r.Id).ToList();

        Assert.Equal(databaseOrder, matcherOrder);
    }
}
