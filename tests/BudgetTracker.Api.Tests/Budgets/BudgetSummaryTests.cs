using System.Net;
using System.Text;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Budgets;

public class BudgetSummaryTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private sealed record World(User User, string Token, Account Account, Account Archived);

    private async Task<World> SetUpAsync()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = new Account { UserId = user.Id, Name = "Everyday" };
        var archived = new Account { UserId = user.Id, Name = "Closed", ArchivedAt = Clock.GetUtcNow() };
        Db.AddRange(account, archived);
        await Db.SaveChangesAsync();
        return new World(user, token, account, archived);
    }

    private async Task<Category> CategoryAsync(User user, string name)
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    private async Task AddTransactionsAsync(World w, params (string Date, decimal Amount, Category? Category, bool Transfer)[] items)
    {
        Db.AddRange(items.Select((i, n) => new Transaction
        {
            UserId = w.User.Id, AccountId = w.Account.Id, CategoryId = i.Category?.Id,
            PostedOn = DateOnly.ParseExact(i.Date, "yyyy-MM-dd"), Amount = i.Amount,
            Description = $"t{n}", IsTransfer = i.Transfer,
        }));
        await Db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> SetBudgetAsync(string token, string month, Category category, decimal amount) =>
        SendAsync(HttpMethod.Put, $"/budgets/{month}/{category.Id}", token, new { amount });

    private async Task<JsonElement> SummaryAsync(string token, string month = "2026-10")
    {
        var response = await SendAsync(HttpMethod.Get, $"/budgets/summary?month={month}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static JsonElement Row(JsonElement summary, string name) =>
        summary.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("categoryName").GetString() == name);

    private static decimal? Money(JsonElement e, string property) =>
        e.GetProperty(property).ValueKind == JsonValueKind.Null ? null : e.GetProperty(property).GetDecimal();

    [Fact]
    public async Task A_realistic_month_adds_up_row_by_row()
    {
        var w = await SetUpAsync();
        var rent = await CategoryAsync(w.User, "Rent");
        var dining = await CategoryAsync(w.User, "Dining");
        var fun = await CategoryAsync(w.User, "Fun");
        var groceries = await CategoryAsync(w.User, "Groceries");
        var salary = await CategoryAsync(w.User, "Salary");
        await SetBudgetAsync(w.Token, "2026-10", rent, 1000m);
        await SetBudgetAsync(w.Token, "2026-10", dining, 200m);
        await SetBudgetAsync(w.Token, "2026-10", groceries, 300m);
        await AddTransactionsAsync(w,
            ("2026-10-01", -1000m, rent, false),
            ("2026-10-05", -150m, dining, false),
            ("2026-10-09", -80m, dining, false),
            ("2026-10-12", -40m, fun, false),
            ("2026-10-15", 2500m, salary, false),
            ("2026-10-20", -25m, null, false),
            ("2026-10-21", -500m, null, true));   // a transfer: not spending

        var summary = await SummaryAsync(w.Token);

        Assert.Equal("2026-10", summary.GetProperty("month").GetString());
        Assert.Equal(["Dining", "Fun", "Groceries", "Rent", "Salary"],
            summary.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("categoryName").GetString()!).ToArray());

        var diningRow = Row(summary, "Dining");
        Assert.Equal((200m, 230m, -30m, true), (Money(diningRow, "budgeted"), Money(diningRow, "spent"), Money(diningRow, "remaining"), diningRow.GetProperty("over").GetBoolean()));
        var rentRow = Row(summary, "Rent");
        Assert.Equal((1000m, 1000m, 0m, false), (Money(rentRow, "budgeted"), Money(rentRow, "spent"), Money(rentRow, "remaining"), rentRow.GetProperty("over").GetBoolean()));
        var funRow = Row(summary, "Fun");
        Assert.Equal((null, 40m, null, false), (Money(funRow, "budgeted"), Money(funRow, "spent"), Money(funRow, "remaining"), funRow.GetProperty("over").GetBoolean()));
        var groceriesRow = Row(summary, "Groceries");
        Assert.Equal((300m, 0m, 300m), (Money(groceriesRow, "budgeted"), Money(groceriesRow, "spent"), Money(groceriesRow, "remaining")));
        Assert.Equal(-2500m, Money(Row(summary, "Salary"), "spent"));

        Assert.Equal(25m, summary.GetProperty("uncategorizedSpent").GetDecimal());
        var totals = summary.GetProperty("totals");
        Assert.Equal(1500m, totals.GetProperty("budgeted").GetDecimal());
        Assert.Equal(1000m + 230m + 40m + 0m - 2500m + 25m, totals.GetProperty("spent").GetDecimal());
    }

    [Fact]
    public async Task The_response_has_a_stable_shape()
    {
        var w = await SetUpAsync();
        var rent = await CategoryAsync(w.User, "Rent");
        await SetBudgetAsync(w.Token, "2026-10", rent, 1000m);

        var summary = await SummaryAsync(w.Token);

        Assert.Equal(
            ["categories", "month", "totals", "uncategorizedSpent"],
            summary.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["budgeted", "categoryId", "categoryName", "over", "remaining", "spent"],
            summary.GetProperty("categories")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["budgeted", "spent"], summary.GetProperty("totals").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_month_is_half_open_so_the_first_and_last_days_are_in_and_the_neighbours_are_out()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await AddTransactionsAsync(w,
            ("2026-09-30", -1m, food, false),
            ("2026-10-01", -10m, food, false),
            ("2026-10-31", -100m, food, false),
            ("2026-11-01", -1000m, food, false));

        var summary = await SummaryAsync(w.Token);

        Assert.Equal(110m, Money(Row(summary, "Food"), "spent"));
    }

    [Fact]
    public async Task February_in_a_leap_year_runs_to_the_twenty_ninth()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await AddTransactionsAsync(w,
            ("2028-02-29", -7m, food, false),
            ("2028-03-01", -900m, food, false));

        var summary = await SummaryAsync(w.Token, "2028-02");

        Assert.Equal(7m, Money(Row(summary, "Food"), "spent"));
    }

    [Fact]
    public async Task December_ends_at_the_new_year_not_at_month_thirteen()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await AddTransactionsAsync(w,
            ("2026-12-31", -5m, food, false),
            ("2027-01-01", -500m, food, false));

        var summary = await SummaryAsync(w.Token, "2026-12");

        Assert.Equal(5m, Money(Row(summary, "Food"), "spent"));
    }

    [Fact]
    public async Task Transfers_are_never_spending_even_when_they_have_a_category()
    {
        var w = await SetUpAsync();
        var card = await CategoryAsync(w.User, "Card payments");
        await AddTransactionsAsync(w, ("2026-10-05", -500m, card, true), ("2026-10-06", -40m, card, false));

        var summary = await SummaryAsync(w.Token);

        Assert.Equal(40m, Money(Row(summary, "Card payments"), "spent"));
        Assert.Equal(40m, summary.GetProperty("totals").GetProperty("spent").GetDecimal());
    }

    [Fact]
    public async Task Marking_a_transaction_as_a_transfer_takes_it_out_of_the_summary()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await AddTransactionsAsync(w, ("2026-10-05", -50m, food, false));
        var id = (await Db.Transactions.AsNoTracking().SingleAsync()).Id;

        await SendAsync(HttpMethod.Put, $"/transactions/{id}", w.Token, new
        {
            accountId = w.Account.Id, categoryId = food.Id, postedOn = "2026-10-05",
            amount = -50m, description = "t0", isTransfer = true,
        });

        Assert.Equal(0m, (await SummaryAsync(w.Token)).GetProperty("totals").GetProperty("spent").GetDecimal());
    }

    [Fact]
    public async Task An_archived_accounts_history_still_counts()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        Db.Add(new Transaction
        {
            UserId = w.User.Id, AccountId = w.Archived.Id, CategoryId = food.Id,
            PostedOn = new DateOnly(2026, 10, 3), Amount = -60m, Description = "old account",
        });
        await Db.SaveChangesAsync();

        var summary = await SummaryAsync(w.Token);

        Assert.Equal(60m, Money(Row(summary, "Food"), "spent"));
    }

    [Fact]
    public async Task Only_the_callers_own_data_is_summarized()
    {
        var w = await SetUpAsync();
        var (bob, bobToken) = await CreateUserWithSessionAsync();
        var bobsAccount = new Account { UserId = bob.Id, Name = "Bobs" };
        var bobsCategory = new Category { UserId = bob.Id, Name = "Bobs food" };
        Db.AddRange(bobsAccount, bobsCategory);
        await Db.SaveChangesAsync();
        Db.Add(new Transaction
        {
            UserId = bob.Id, AccountId = bobsAccount.Id, CategoryId = bobsCategory.Id,
            PostedOn = new DateOnly(2026, 10, 3), Amount = -999m, Description = "bobs",
        });
        await Db.SaveChangesAsync();
        await SetBudgetAsync(bobToken, "2026-10", bobsCategory, 5m);

        var summary = await SummaryAsync(w.Token);

        Assert.Empty(summary.GetProperty("categories").EnumerateArray());
        Assert.Equal(0m, summary.GetProperty("totals").GetProperty("spent").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("totals").GetProperty("budgeted").GetDecimal());
    }

    [Fact]
    public async Task A_month_with_nothing_in_it_is_a_summary_of_zeros()
    {
        var w = await SetUpAsync();

        var summary = await SummaryAsync(w.Token, "2030-01");

        Assert.Empty(summary.GetProperty("categories").EnumerateArray());
        Assert.Equal(0m, summary.GetProperty("uncategorizedSpent").GetDecimal());
        Assert.Equal((0m, 0m), (summary.GetProperty("totals").GetProperty("budgeted").GetDecimal(), summary.GetProperty("totals").GetProperty("spent").GetDecimal()));
    }

    [Fact]
    public async Task A_budget_for_another_month_does_not_appear()
    {
        var w = await SetUpAsync();
        var rent = await CategoryAsync(w.User, "Rent");
        await SetBudgetAsync(w.Token, "2026-09", rent, 1000m);

        Assert.Empty((await SummaryAsync(w.Token, "2026-10")).GetProperty("categories").EnumerateArray());
    }

    [Fact]
    public async Task Deleting_a_category_moves_its_spending_to_uncategorized_and_drops_its_budget()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await SetBudgetAsync(w.Token, "2026-10", food, 300m);
        await AddTransactionsAsync(w, ("2026-10-05", -50m, food, false));
        await SendAsync(HttpMethod.Delete, $"/categories/{food.Id}", w.Token);

        var summary = await SummaryAsync(w.Token);

        Assert.Empty(summary.GetProperty("categories").EnumerateArray());
        Assert.Equal(50m, summary.GetProperty("uncategorizedSpent").GetDecimal());
        Assert.Equal((0m, 50m), (summary.GetProperty("totals").GetProperty("budgeted").GetDecimal(), summary.GetProperty("totals").GetProperty("spent").GetDecimal()));
    }

    [Fact]
    public async Task The_total_spent_matches_the_transaction_list_for_the_same_month()
    {
        // Two independent routes to the same number: the summary's grouped query, and the
        // listing endpoint paged through and added up. They must agree to the cent.
        var w = await SetUpAsync();
        var a = await CategoryAsync(w.User, "A");
        var b = await CategoryAsync(w.User, "B");
        var items = Enumerable.Range(0, 180).Select(i =>
            ($"2026-10-{1 + i % 31:00}", -(1.25m + i % 17), i % 3 == 0 ? a : i % 3 == 1 ? b : null, i % 11 == 0)).ToArray();
        await AddTransactionsAsync(w, items);

        var summary = await SummaryAsync(w.Token);

        var listed = 0m;
        string? cursor = null;
        do
        {
            var page = await ReadJsonAsync(await SendAsync(HttpMethod.Get,
                $"/transactions?from=2026-10-01&to=2026-10-31&isTransfer=false&limit=50{(cursor is null ? "" : $"&cursor={cursor}")}", w.Token));
            listed += page.GetProperty("items").EnumerateArray().Sum(t => t.GetProperty("amount").GetDecimal());
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(-listed, summary.GetProperty("totals").GetProperty("spent").GetDecimal());
    }

    [Fact]
    public async Task An_import_with_rules_flows_all_the_way_into_the_summary()
    {
        var w = await SetUpAsync();
        var groceries = await CategoryAsync(w.User, "Groceries");
        var dining = await CategoryAsync(w.User, "Dining");
        Db.AddRange(
            new Rule { UserId = w.User.Id, Position = 0, Pattern = "kroger", MatchType = RuleMatchType.Contains, CategoryId = groceries.Id },
            new Rule { UserId = w.User.Id, Position = 1, Pattern = "starbucks", MatchType = RuleMatchType.Contains, CategoryId = dining.Id },
            new Rule { UserId = w.User.Id, Position = 2, Pattern = "payment thank you", MatchType = RuleMatchType.Contains, MarkTransfer = true });
        await Db.SaveChangesAsync();
        await SetBudgetAsync(w.Token, "2026-10", groceries, 100m);
        var csv = "Date,Description,Amount\n2026-10-01,KROGER #55,-60.00\n2026-10-02,STARBUCKS #12,-4.50\n2026-10-03,KROGER #55,-55.00\n2026-10-04,PAYMENT THANK YOU,500.00\n2026-10-05,Mystery shop,-10.00\n";

        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{w.Account.Id}/imports")
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv"),
        };
        request.Headers.Authorization = new("Bearer", w.Token);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(request)).StatusCode);

        var summary = await SummaryAsync(w.Token);

        var groceriesRow = Row(summary, "Groceries");
        Assert.Equal((100m, 115m, -15m, true),
            (Money(groceriesRow, "budgeted"), Money(groceriesRow, "spent"), Money(groceriesRow, "remaining"), groceriesRow.GetProperty("over").GetBoolean()));
        Assert.Equal(4.50m, Money(Row(summary, "Dining"), "spent"));
        Assert.Equal(10m, summary.GetProperty("uncategorizedSpent").GetDecimal());
        Assert.Equal(129.50m, summary.GetProperty("totals").GetProperty("spent").GetDecimal());   // the 500.00 payment is a transfer
    }

    [Fact]
    public async Task Applying_rules_later_moves_spending_out_of_uncategorized()
    {
        var w = await SetUpAsync();
        var food = await CategoryAsync(w.User, "Food");
        await AddTransactionsAsync(w, ("2026-10-05", -50m, null, false));
        Db.Add(new Rule { UserId = w.User.Id, Position = 0, Pattern = "t0", MatchType = RuleMatchType.Equals, CategoryId = food.Id });
        await Db.SaveChangesAsync();
        Assert.Equal(50m, (await SummaryAsync(w.Token)).GetProperty("uncategorizedSpent").GetDecimal());

        await SendAsync(HttpMethod.Post, "/rules/apply", w.Token);

        var summary = await SummaryAsync(w.Token);
        Assert.Equal(0m, summary.GetProperty("uncategorizedSpent").GetDecimal());
        Assert.Equal(50m, Money(Row(summary, "Food"), "spent"));
    }

    [Theory]
    [InlineData("", "month.required")]
    [InlineData("?month=", "month.required")]
    [InlineData("?month=2026-1", "month.invalid")]
    [InlineData("?month=2026-10-01", "month.invalid")]
    public async Task The_month_must_be_given_as_yyyy_MM(string query, string code)
    {
        var w = await SetUpAsync();

        var response = await SendAsync(HttpMethod.Get, "/budgets/summary" + query, w.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("codes").GetProperty("month")[0].GetString());
    }

    [Fact]
    public async Task The_summary_needs_a_session()
    {
        var response = await SendAsync(HttpMethod.Get, "/budgets/summary?month=2026-10", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
