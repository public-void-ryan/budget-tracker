using System.Net;
using System.Text;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Imports;

public class ImportRulesTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<(User User, string Token, Account Account, Category Groceries, Category Dining)> SetUpAsync()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = new Account { UserId = user.Id, Name = "Everyday" };
        var groceries = new Category { UserId = user.Id, Name = "Groceries" };
        var dining = new Category { UserId = user.Id, Name = "Dining" };
        Db.AddRange(account, groceries, dining);
        await Db.SaveChangesAsync();
        return (user, token, account, groceries, dining);
    }

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

    private static string Csv(params string[] rows) => "Date,Description,Amount\n" + string.Concat(rows.Select(r => r + "\n"));

    private async Task<JsonElement> ImportAsync(string token, Guid accountId, string csv, bool dryRun = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{accountId}/imports{(dryRun ? "?dryRun=true" : "")}")
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv"),
        };
        request.Headers.Authorization = new("Bearer", token);
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static List<JsonElement> Rows(JsonElement summary) => [.. summary.GetProperty("rows").EnumerateArray()];

    private static Guid? CategoryOf(JsonElement row) =>
        row.GetProperty("categoryId").ValueKind == JsonValueKind.Null ? null : row.GetProperty("categoryId").GetGuid();

    [Fact]
    public async Task A_preview_shows_the_category_each_new_row_would_get()
    {
        var (user, token, account, groceries, dining) = await SetUpAsync();
        await AddRuleAsync(user, 0, "starbucks", dining);
        await AddRuleAsync(user, 1, "kroger", groceries);

        var summary = await ImportAsync(token, account.Id,
            Csv("2026-10-01,STARBUCKS #12,-4.50", "2026-10-02,KROGER 55,-30.00", "2026-10-03,Mystery,-9.00"), dryRun: true);

        var rows = Rows(summary);
        Assert.Equal([dining.Id, groceries.Id, (Guid?)null], rows.Select(CategoryOf).ToArray());
        Assert.Equal(2, summary.GetProperty("categorized").GetInt32());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_commit_stores_the_category_and_the_transfer_mark_from_the_first_matching_rule()
    {
        var (user, token, account, groceries, _) = await SetUpAsync();
        await AddRuleAsync(user, 0, "payment thank you", category: null, transfer: true);
        await AddRuleAsync(user, 1, "kroger", groceries);
        await AddRuleAsync(user, 2, "costco", groceries, transfer: true);

        var summary = await ImportAsync(token, account.Id,
            Csv("2026-10-01,PAYMENT THANK YOU,500.00", "2026-10-02,KROGER 55,-30.00", "2026-10-03,COSTCO,-80.00", "2026-10-04,Mystery,-9.00"));

        Assert.Equal((2, 2), (summary.GetProperty("categorized").GetInt32(), summary.GetProperty("markedAsTransfer").GetInt32()));
        var stored = await Db.Transactions.AsNoTracking().OrderBy(t => t.PostedOn).ToListAsync();
        Assert.Equal(
            [(null, true), (groceries.Id, false), (groceries.Id, true), (null, false)],
            stored.Select(t => (t.CategoryId, t.IsTransfer)).ToArray());
    }

    [Fact]
    public async Task The_first_rule_in_order_wins_and_only_its_action_applies()
    {
        var (user, token, account, groceries, dining) = await SetUpAsync();
        await AddRuleAsync(user, 0, "amazon prime", dining, type: RuleMatchType.StartsWith);
        await AddRuleAsync(user, 1, "amazon", groceries, transfer: true);

        await ImportAsync(token, account.Id, Csv("2026-10-01,AMAZON PRIME VIDEO,-9.99"));

        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal((dining.Id, false), (stored.CategoryId, stored.IsTransfer));
    }

    [Fact]
    public async Task Rows_that_are_already_stored_are_never_touched_by_a_later_import()
    {
        var (user, token, account, groceries, dining) = await SetUpAsync();
        var file = Csv("2026-10-01,KROGER 55,-30.00");
        await AddRuleAsync(user, 0, "kroger", groceries);
        await ImportAsync(token, account.Id, file);

        // The rule now says something else, and the same file is uploaded again.
        await Db.Rules.ExecuteDeleteAsync();
        await AddRuleAsync(user, 0, "kroger", dining);
        var again = await ImportAsync(token, account.Id, file);

        Assert.Equal(["duplicate"], Rows(again).Select(r => r.GetProperty("status").GetString()!).ToArray());
        Assert.Equal([(Guid?)null], Rows(again).Select(CategoryOf).ToArray());
        Assert.Equal(groceries.Id, (await Db.Transactions.AsNoTracking().SingleAsync()).CategoryId);
    }

    [Fact]
    public async Task New_rows_in_a_later_file_get_the_rules_as_they_are_then()
    {
        var (user, token, account, groceries, dining) = await SetUpAsync();
        await AddRuleAsync(user, 0, "kroger", groceries);
        await ImportAsync(token, account.Id, Csv("2026-10-01,KROGER 55,-30.00"));
        await Db.Rules.ExecuteDeleteAsync();
        await AddRuleAsync(user, 0, "kroger", dining);

        await ImportAsync(token, account.Id, Csv("2026-10-01,KROGER 55,-30.00", "2026-10-02,KROGER 60,-12.00"));

        var stored = await Db.Transactions.AsNoTracking().OrderBy(t => t.PostedOn).ToListAsync();
        Assert.Equal([groceries.Id, dining.Id], stored.Select(t => t.CategoryId!.Value).ToArray());
    }

    [Fact]
    public async Task With_no_rules_everything_stays_uncategorized()
    {
        var (_, token, account, _, _) = await SetUpAsync();

        var summary = await ImportAsync(token, account.Id, Csv("2026-10-01,KROGER 55,-30.00"));

        Assert.Equal((0, 0), (summary.GetProperty("categorized").GetInt32(), summary.GetProperty("markedAsTransfer").GetInt32()));
        Assert.Null((await Db.Transactions.AsNoTracking().SingleAsync()).CategoryId);
    }

    [Fact]
    public async Task Another_users_rules_are_never_applied()
    {
        var (_, token, account, _, _) = await SetUpAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsCategory = new Category { UserId = bob.Id, Name = "Bobs" };
        Db.Add(bobsCategory);
        await Db.SaveChangesAsync();
        await AddRuleAsync(bob, 0, "kroger", bobsCategory);

        await ImportAsync(token, account.Id, Csv("2026-10-01,KROGER 55,-30.00"));

        Assert.Null((await Db.Transactions.AsNoTracking().SingleAsync()).CategoryId);
    }

    [Fact]
    public async Task A_deleted_category_takes_its_rules_with_it_so_they_stop_applying()
    {
        var (user, token, account, groceries, _) = await SetUpAsync();
        await AddRuleAsync(user, 0, "kroger", groceries);
        await Db.Categories.Where(c => c.Id == groceries.Id).ExecuteDeleteAsync();

        var summary = await ImportAsync(token, account.Id, Csv("2026-10-01,KROGER 55,-30.00"));

        Assert.Equal(0, summary.GetProperty("categorized").GetInt32());
    }

    [Fact]
    public async Task Rules_and_the_sign_setting_work_together()
    {
        var (user, token, _, groceries, _) = await SetUpAsync();
        var card = new Account { UserId = user.Id, Name = "Visa", InvertSign = true };
        Db.Add(card);
        await Db.SaveChangesAsync();
        await AddRuleAsync(user, 0, "kroger", groceries);

        await ImportAsync(token, card.Id, Csv("2026-10-01,KROGER 55,30.00"));

        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal((-30.00m, groceries.Id), (stored.Amount, stored.CategoryId));
    }
}
