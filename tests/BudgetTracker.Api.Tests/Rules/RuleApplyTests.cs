using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Rules;

public class RuleApplyTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private sealed record World(
        User User, string Token, Account Open, Account Archived, Category Groceries, Category Dining);

    private async Task<World> SetUpAsync()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var open = new Account { UserId = user.Id, Name = "Open" };
        var archived = new Account { UserId = user.Id, Name = "Closed", ArchivedAt = Clock.GetUtcNow() };
        var groceries = new Category { UserId = user.Id, Name = "Groceries" };
        var dining = new Category { UserId = user.Id, Name = "Dining" };
        Db.AddRange(open, archived, groceries, dining);
        await Db.SaveChangesAsync();
        return new World(user, token, open, archived, groceries, dining);
    }

    private static Transaction Tx(
        User user, Account account, string description, Category? category = null, bool transfer = false) => new()
    {
        UserId = user.Id,
        AccountId = account.Id,
        CategoryId = category?.Id,
        PostedOn = new DateOnly(2026, 10, 1),
        Amount = -5m,
        Description = description,
        IsTransfer = transfer,
    };

    private async Task AddRuleAsync(
        User user, int position, string pattern, Category? category, bool transfer = false,
        RuleMatchType type = RuleMatchType.Contains)
    {
        Db.Add(new Rule
        {
            UserId = user.Id, Position = position, Pattern = pattern, MatchType = type,
            CategoryId = category?.Id, MarkTransfer = transfer,
        });
        await Db.SaveChangesAsync();
    }

    private async Task<JsonElement> ApplyAsync(string token, bool dryRun = false)
    {
        var response = await SendAsync(HttpMethod.Post, $"/rules/apply{(dryRun ? "?dryRun=true" : "")}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static (int Examined, int Categorized, int Marked, int Skipped) Counts(JsonElement r) => (
        r.GetProperty("examined").GetInt32(), r.GetProperty("categorized").GetInt32(),
        r.GetProperty("markedAsTransfer").GetInt32(), r.GetProperty("skippedArchived").GetInt32());

    private async Task<(Guid? Category, bool Transfer)> StateOfAsync(string description)
    {
        var row = await Db.Transactions.AsNoTracking()
            .Where(t => t.Description == description)
            .Select(t => new { t.CategoryId, t.IsTransfer })
            .SingleAsync();
        return (row.CategoryId, row.IsTransfer);
    }

    // What gets filled in

    [Fact]
    public async Task Uncategorized_transactions_that_match_get_the_rules_category()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "KROGER 55"), Tx(w.User, w.Open, "Mystery"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((2, 1, 0, 0), Counts(result));
        Assert.Equal((w.Groceries.Id, false), await StateOfAsync("KROGER 55"));
        Assert.Equal(((Guid?)null, false), await StateOfAsync("Mystery"));
    }

    [Fact]
    public async Task Nothing_a_person_already_decided_is_ever_overwritten()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(
            Tx(w.User, w.Open, "KROGER already dining", category: w.Dining),
            Tx(w.User, w.Open, "KROGER a transfer", transfer: true),
            Tx(w.User, w.Open, "KROGER blank"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((1, 1, 0, 0), Counts(result));
        Assert.Equal((w.Dining.Id, false), await StateOfAsync("KROGER already dining"));
        Assert.Equal(((Guid?)null, true), await StateOfAsync("KROGER a transfer"));
        Assert.Equal((w.Groceries.Id, false), await StateOfAsync("KROGER blank"));
    }

    [Fact]
    public async Task A_transfer_rule_marks_transfers_and_a_rule_can_do_both()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "payment thank you", category: null, transfer: true);
        await AddRuleAsync(w.User, 1, "costco", w.Groceries, transfer: true);
        Db.AddRange(Tx(w.User, w.Open, "PAYMENT THANK YOU"), Tx(w.User, w.Open, "COSTCO WHOLESALE"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((2, 1, 2, 0), Counts(result));
        Assert.Equal(((Guid?)null, true), await StateOfAsync("PAYMENT THANK YOU"));
        Assert.Equal((w.Groceries.Id, true), await StateOfAsync("COSTCO WHOLESALE"));
    }

    [Fact]
    public async Task The_first_rule_in_order_wins()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "amazon prime", w.Dining, type: RuleMatchType.StartsWith);
        await AddRuleAsync(w.User, 1, "amazon", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "AMAZON PRIME VIDEO"), Tx(w.User, w.Open, "AMAZON MARKETPLACE"));
        await Db.SaveChangesAsync();

        await ApplyAsync(w.Token);

        Assert.Equal((w.Dining.Id, false), await StateOfAsync("AMAZON PRIME VIDEO"));
        Assert.Equal((w.Groceries.Id, false), await StateOfAsync("AMAZON MARKETPLACE"));
    }

    [Fact]
    public async Task Manual_and_imported_transactions_are_treated_alike()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        var imported = Tx(w.User, w.Open, "KROGER imported");
        imported.ImportHash = new string('a', 64);
        imported.ImportSeq = 0;
        Db.AddRange(imported, Tx(w.User, w.Open, "KROGER manual"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((2, 2, 0, 0), Counts(result));
    }

    [Fact]
    public async Task Only_the_category_and_transfer_mark_change()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        var original = Tx(w.User, w.Open, "KROGER 55");
        Db.Add(original);
        await Db.SaveChangesAsync();
        var before = await Db.Transactions.AsNoTracking().SingleAsync();

        await ApplyAsync(w.Token);

        var after = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal(
            (before.Id, before.AccountId, before.PostedOn, before.Amount, before.Description, before.CreatedAt),
            (after.Id, after.AccountId, after.PostedOn, after.Amount, after.Description, after.CreatedAt));
    }

    // Archived accounts, other users, dry runs

    [Fact]
    public async Task Archived_accounts_are_left_alone_and_the_skipped_count_says_so()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "KROGER open"), Tx(w.User, w.Archived, "KROGER closed"), Tx(w.User, w.Archived, "Mystery closed"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((1, 1, 0, 1), Counts(result));
        Assert.Equal(((Guid?)null, false), await StateOfAsync("KROGER closed"));
    }

    [Fact]
    public async Task Unarchiving_an_account_lets_the_next_apply_reach_it()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.Add(Tx(w.User, w.Archived, "KROGER closed"));
        await Db.SaveChangesAsync();
        await ApplyAsync(w.Token);
        await SendAsync(HttpMethod.Post, $"/accounts/{w.Archived.Id}/unarchive", w.Token);

        var result = await ApplyAsync(w.Token);

        Assert.Equal((1, 1, 0, 0), Counts(result));
        Assert.Equal((w.Groceries.Id, false), await StateOfAsync("KROGER closed"));
    }

    [Fact]
    public async Task Other_users_transactions_are_never_touched()
    {
        var w = await SetUpAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsAccount = new Account { UserId = bob.Id, Name = "Bobs" };
        Db.Add(bobsAccount);
        await Db.SaveChangesAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "KROGER mine"), Tx(bob, bobsAccount, "KROGER bobs"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal(1, result.GetProperty("examined").GetInt32());
        Assert.Equal(((Guid?)null, false), await StateOfAsync("KROGER bobs"));
    }

    [Fact]
    public async Task A_dry_run_reports_exactly_what_a_real_run_then_does_and_writes_nothing()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "payment", category: null, transfer: true);
        await AddRuleAsync(w.User, 1, "kroger", w.Groceries);
        Db.AddRange(
            Tx(w.User, w.Open, "PAYMENT 1"), Tx(w.User, w.Open, "KROGER 1"), Tx(w.User, w.Open, "KROGER 2"),
            Tx(w.User, w.Open, "Mystery"), Tx(w.User, w.Archived, "KROGER closed"));
        await Db.SaveChangesAsync();

        var preview = await ApplyAsync(w.Token, dryRun: true);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, await Db.Transactions.CountAsync(t => t.CategoryId != null || t.IsTransfer));

        var real = await ApplyAsync(w.Token);

        Assert.False(real.GetProperty("dryRun").GetBoolean());
        Assert.Equal(Counts(preview), Counts(real));
        Assert.Equal((4, 2, 1, 1), Counts(real));
    }

    [Fact]
    public async Task The_response_says_how_many_each_rule_claimed()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        await AddRuleAsync(w.User, 1, "starbucks", w.Dining);
        await AddRuleAsync(w.User, 2, "never matches", w.Dining);
        Db.AddRange(Tx(w.User, w.Open, "KROGER 1"), Tx(w.User, w.Open, "KROGER 2"), Tx(w.User, w.Open, "STARBUCKS"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal(
            [("kroger", 2), ("starbucks", 1)],
            result.GetProperty("byRule").EnumerateArray()
                .Select(h => (h.GetProperty("pattern").GetString()!, h.GetProperty("matched").GetInt32())).ToArray());
    }

    [Fact]
    public async Task With_no_rules_nothing_is_examined_or_changed()
    {
        var w = await SetUpAsync();
        Db.Add(Tx(w.User, w.Open, "KROGER 55"));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((0, 0, 0, 0), Counts(result));
        Assert.Empty(result.GetProperty("byRule").EnumerateArray());
    }

    // Repeating it, batching, concurrency

    [Fact]
    public async Task Applying_twice_changes_nothing_the_second_time()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "KROGER 1"), Tx(w.User, w.Open, "Mystery"));
        await Db.SaveChangesAsync();
        await ApplyAsync(w.Token);

        var second = await ApplyAsync(w.Token);

        Assert.Equal((1, 0, 0, 0), Counts(second));
    }

    [Fact]
    public async Task Editing_a_rule_and_applying_again_reaches_only_what_is_still_blank()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "kroger", w.Groceries);
        Db.AddRange(Tx(w.User, w.Open, "KROGER 1"), Tx(w.User, w.Open, "STARBUCKS 1"));
        await Db.SaveChangesAsync();
        await ApplyAsync(w.Token);
        await AddRuleAsync(w.User, 1, "starbucks", w.Dining);

        var second = await ApplyAsync(w.Token);

        Assert.Equal((1, 1, 0, 0), Counts(second));
        Assert.Equal((w.Groceries.Id, false), await StateOfAsync("KROGER 1"));
        Assert.Equal((w.Dining.Id, false), await StateOfAsync("STARBUCKS 1"));
    }

    [Fact]
    public async Task Thousands_of_transactions_are_processed_across_several_batches()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "starbucks", w.Dining);
        Db.AddRange(Enumerable.Range(0, 2_600).Select(i => Tx(w.User, w.Open, $"STARBUCKS #{i}")));
        Db.AddRange(Enumerable.Range(0, 100).Select(i => Tx(w.User, w.Open, $"OTHER {i}")));
        await Db.SaveChangesAsync();

        var result = await ApplyAsync(w.Token);

        Assert.Equal((2_700, 2_600, 0, 0), Counts(result));
        Assert.Equal(2_600, await Db.Transactions.CountAsync(t => t.CategoryId == w.Dining.Id));
        Assert.Equal(100, await Db.Transactions.CountAsync(t => t.CategoryId == null));
    }

    [Fact]
    public async Task Simultaneous_applies_change_each_transaction_exactly_once()
    {
        var w = await SetUpAsync();
        await AddRuleAsync(w.User, 0, "starbucks", w.Dining);
        Db.AddRange(Enumerable.Range(0, 1_500).Select(i => Tx(w.User, w.Open, $"STARBUCKS #{i}")));
        await Db.SaveChangesAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ApplyAsync(w.Token)));

        Assert.Equal(1_500, results.Sum(r => r.GetProperty("categorized").GetInt32()));
        Assert.Equal(1_500, await Db.Transactions.CountAsync(t => t.CategoryId == w.Dining.Id));
    }

    // Authentication

    [Fact]
    public async Task Applying_needs_a_session()
    {
        var response = await SendAsync(HttpMethod.Post, "/rules/apply", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
