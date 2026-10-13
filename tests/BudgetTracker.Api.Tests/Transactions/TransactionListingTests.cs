using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Transactions;

public class TransactionListingTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<Account> AddAccountAsync(User user, string name = "Checking")
    {
        var account = new Account { UserId = user.Id, Name = name };
        Db.Add(account);
        await Db.SaveChangesAsync();
        return account;
    }

    private async Task<Category> AddCategoryAsync(User user, string name)
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    private static Transaction NewTransaction(
        User user, Account account, string date, string description = "t", Category? category = null,
        bool isTransfer = false) => new()
    {
        UserId = user.Id,
        AccountId = account.Id,
        CategoryId = category?.Id,
        PostedOn = DateOnly.ParseExact(date, "yyyy-MM-dd"),
        Amount = -1m,
        Description = description,
        IsTransfer = isTransfer,
    };

    private async Task<Transaction> AddAsync(
        User user, Account account, string date, string description = "t", Category? category = null,
        bool isTransfer = false)
    {
        var transaction = NewTransaction(user, account, date, description, category, isTransfer);
        Db.Add(transaction);
        await Db.SaveChangesAsync();
        return transaction;
    }

    private async Task<JsonElement> ListAsync(string token, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, "/transactions" + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static string[] Descriptions(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("description").GetString()!)];

    private static Guid[] Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid())];

    private static string? NextCursor(JsonElement page) =>
        page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCursor").GetString();

    /// <summary>
    /// The newest-first order as the DATABASE sorts it. Asking the database, rather than sorting
    /// in C#, keeps the expectation independent of the code under test.
    /// </summary>
    private async Task<Guid[]> ExpectedOrderAsync(User user) =>
        [.. await Db.Transactions
            .Where(t => t.UserId == user.Id)
            .OrderByDescending(t => t.PostedOn)
            .ThenByDescending(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync()];

    // Shape and order

    [Fact]
    public async Task An_empty_list_has_no_items_and_no_next_cursor()
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var page = await ListAsync(token);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Null(NextCursor(page));
        Assert.Equal(["items", "nextCursor"], page.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Transactions_are_newest_first_and_only_the_callers_appear()
    {
        var (alice, token) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(alice);
        var bobsAccount = await AddAccountAsync(bob);
        await AddAsync(alice, account, "2026-10-02", "middle");
        await AddAsync(alice, account, "2026-10-03", "newest");
        await AddAsync(alice, account, "2026-10-01", "oldest");
        await AddAsync(bob, bobsAccount, "2026-10-04", "bobs");

        var page = await ListAsync(token);

        Assert.Equal(["newest", "middle", "oldest"], Descriptions(page));
    }

    // Pagination

    [Fact]
    public async Task Paging_through_visits_every_transaction_exactly_once_including_ties()
    {
        // Many rows share a date, so ordering within a date rests entirely on the id
        // tie-break; a wrong cursor comparison would repeat or skip rows at page boundaries.
        var (user, token) = await CreateUserWithSessionAsync();
        var (other, _) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        var otherAccount = await AddAccountAsync(other);
        var dates = new[] { "2026-10-01", "2026-10-02", "2026-10-02", "2026-10-02", "2026-10-03" };
        Db.AddRange(Enumerable.Range(0, 23).Select(i => NewTransaction(user, account, dates[i % dates.Length], $"t{i}")));
        Db.AddRange(Enumerable.Range(0, 7).Select(i => NewTransaction(other, otherAccount, "2026-10-02", $"o{i}")));
        await Db.SaveChangesAsync();

        var collected = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await ListAsync(token, $"?limit=5{(cursor is null ? "" : $"&cursor={cursor}")}");
            collected.AddRange(Ids(page));
            cursor = NextCursor(page);
            pages++;
        }
        while (cursor is not null && pages < 20);

        Assert.Equal(5, pages);
        Assert.Equal(await ExpectedOrderAsync(user), collected);
        Assert.Equal(23, collected.Distinct().Count());
    }

    [Fact]
    public async Task The_last_page_has_no_next_cursor_even_when_it_is_exactly_full()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        Db.AddRange(Enumerable.Range(0, 10).Select(i => NewTransaction(user, account, "2026-10-01", $"t{i}")));
        await Db.SaveChangesAsync();

        var first = await ListAsync(token, "?limit=5");
        var second = await ListAsync(token, $"?limit=5&cursor={NextCursor(first)}");

        Assert.NotNull(NextCursor(first));
        Assert.Equal(5, Ids(second).Length);
        Assert.Null(NextCursor(second));
    }

    [Fact]
    public async Task A_transaction_added_between_pages_causes_no_repeats_and_no_gaps()
    {
        // The reason for keyset pagination: with offsets, a new newest row would shift every
        // later page by one and repeat an item.
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        Db.AddRange(Enumerable.Range(0, 9).Select(i => NewTransaction(user, account, $"2026-09-{i + 1:00}", $"t{i}")));
        await Db.SaveChangesAsync();
        var expected = await ExpectedOrderAsync(user);

        var first = await ListAsync(token, "?limit=3");
        await AddAsync(user, account, "2026-10-15", "arrived in between");
        var second = await ListAsync(token, $"?limit=3&cursor={NextCursor(first)}");

        Assert.Equal(expected[..3], Ids(first));
        Assert.Equal(expected[3..6], Ids(second));
    }

    [Fact]
    public async Task The_default_page_is_fifty_and_the_limit_is_clamped_to_a_hundred()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        Db.AddRange(Enumerable.Range(0, 105).Select(i => NewTransaction(user, account, "2026-10-01", $"t{i}")));
        await Db.SaveChangesAsync();

        var byDefault = await ListAsync(token);
        var greedy = await ListAsync(token, "?limit=1000");
        var rest = await ListAsync(token, $"?limit=1000&cursor={NextCursor(greedy)}");

        Assert.Equal(50, Ids(byDefault).Length);
        Assert.NotNull(NextCursor(byDefault));
        Assert.Equal(100, Ids(greedy).Length);
        Assert.Equal(5, Ids(rest).Length);
        Assert.Null(NextCursor(rest));
    }

    [Theory]
    [InlineData("?limit=0", "limit", "limit.invalid")]
    [InlineData("?limit=-5", "limit", "limit.invalid")]
    [InlineData("?cursor=garbage", "cursor", "cursor.invalid")]
    [InlineData("?from=2026-1-1", "from", "from.invalid")]
    [InlineData("?to=yesterday", "to", "to.invalid")]
    [InlineData("?from=2026-10-05&to=2026-10-01", "to", "to.before_from")]
    public async Task Bad_query_parameters_are_a_400_naming_the_parameter(string query, string field, string code)
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var response = await SendAsync(HttpMethod.Get, "/transactions" + query, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("codes").GetProperty(field)[0].GetString());
    }

    [Fact]
    public async Task A_cursor_from_another_users_listing_only_moves_within_your_own_data()
    {
        // Cursors are not signed: a forged one can only choose a position in the caller's own
        // rows, never reveal anyone else's.
        var (alice, aliceToken) = await CreateUserWithSessionAsync();
        var (bob, bobToken) = await CreateUserWithSessionAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var bobAccount = await AddAccountAsync(bob);
        Db.AddRange(Enumerable.Range(0, 6).Select(i => NewTransaction(bob, bobAccount, $"2026-10-{i + 1:00}", $"bob{i}")));
        await AddAsync(alice, aliceAccount, "2026-09-01", "alice");
        await Db.SaveChangesAsync();
        var bobsPage = await ListAsync(bobToken, "?limit=2");

        var withBobsCursor = await ListAsync(aliceToken, $"?cursor={NextCursor(bobsPage)}");

        Assert.Equal(["alice"], Descriptions(withBobsCursor));
    }

    // Filters

    private async Task<(string Token, Account A, Account B, Category X, Category Y)> SeedFilterDataAsync()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var a = await AddAccountAsync(user, "A");
        var b = await AddAccountAsync(user, "B");
        var x = await AddCategoryAsync(user, "X");
        var y = await AddCategoryAsync(user, "Y");
        await AddAsync(user, a, "2026-10-01", "a-x-1", x);
        await AddAsync(user, a, "2026-10-02", "a-y-2", y);
        await AddAsync(user, a, "2026-10-03", "a-none-3");
        await AddAsync(user, b, "2026-10-02", "b-x-2", x);
        await AddAsync(user, b, "2026-10-04", "b-y-4-transfer", y, isTransfer: true);
        return (token, a, b, x, y);
    }

    [Fact]
    public async Task Filtering_by_account()
    {
        var (token, a, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, $"?accountId={a.Id}");

        Assert.Equal(["a-none-3", "a-y-2", "a-x-1"], Descriptions(page));
    }

    [Fact]
    public async Task Filtering_by_category()
    {
        var (token, _, _, x, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, $"?categoryId={x.Id}");

        Assert.Equal(["b-x-2", "a-x-1"], Descriptions(page));
    }

    [Fact]
    public async Task Filtering_by_uncategorized()
    {
        var (token, _, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, "?uncategorized=true");

        Assert.Equal(["a-none-3"], Descriptions(page));
    }

    [Fact]
    public async Task Filtering_by_categorized_is_the_complement()
    {
        var (token, _, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, "?uncategorized=false");

        Assert.Equal(["b-y-4-transfer", "b-x-2", "a-y-2", "a-x-1"], Descriptions(page));
    }

    [Fact]
    public async Task Uncategorized_combines_with_the_other_filters()
    {
        var (token, a, b, _, _) = await SeedFilterDataAsync();

        Assert.Equal(["a-none-3"], Descriptions(await ListAsync(token, $"?uncategorized=true&accountId={a.Id}")));
        Assert.Empty(Descriptions(await ListAsync(token, $"?uncategorized=true&accountId={b.Id}")));
        Assert.Equal(["a-none-3"], Descriptions(await ListAsync(token, "?uncategorized=true&from=2026-10-03&to=2026-10-03")));
        Assert.Empty(Descriptions(await ListAsync(token, "?uncategorized=true&isTransfer=true")));
    }

    [Fact]
    public async Task Uncategorized_pages_by_keyset_like_any_other_filter()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        var category = await AddCategoryAsync(user, "Food");
        for (var i = 1; i <= 7; i++)
        {
            await AddAsync(user, account, $"2026-10-{i:00}", $"none-{i}");
            await AddAsync(user, account, $"2026-10-{i:00}", $"food-{i}", category);
        }

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(token, $"?uncategorized=true&limit=3{(cursor is null ? "" : $"&cursor={cursor}")}");
            seen.AddRange(Descriptions(page));
            cursor = NextCursor(page);
        }
        while (cursor is not null);

        Assert.Equal(7, seen.Count);
        Assert.All(seen, d => Assert.StartsWith("none-", d));
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    [Fact]
    public async Task Uncategorized_together_with_a_category_is_refused()
    {
        var (token, _, _, x, _) = await SeedFilterDataAsync();

        var response = await SendAsync(HttpMethod.Get, $"/transactions?uncategorized=true&categoryId={x.Id}", token);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("uncategorized.conflicts_with_category",
            (await ReadJsonAsync(response)).GetProperty("codes").GetProperty("uncategorized")[0].GetString());
    }

    [Fact]
    public async Task Uncategorized_false_with_a_category_is_allowed_and_redundant()
    {
        var (token, _, _, x, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, $"?uncategorized=false&categoryId={x.Id}");

        Assert.Equal(["b-x-2", "a-x-1"], Descriptions(page));
    }

    [Fact]
    public async Task Uncategorized_never_shows_another_users_transactions()
    {
        var (_, aliceToken) = await CreateUserWithSessionAsync("alice@example.com");
        var (bob, _) = await CreateUserWithSessionAsync("bob@example.com");
        var bobsAccount = await AddAccountAsync(bob);
        await AddAsync(bob, bobsAccount, "2026-10-01", "bobs-uncategorized");

        var page = await ListAsync(aliceToken, "?uncategorized=true");

        Assert.Empty(Descriptions(page));
    }

    [Fact]
    public async Task The_date_range_is_inclusive_at_both_ends()
    {
        var (token, _, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, "?from=2026-10-02&to=2026-10-03");

        Assert.Equal(["a-none-3", "b-x-2", "a-y-2"], Descriptions(page));
    }

    [Fact]
    public async Task One_day_can_be_selected_with_equal_from_and_to()
    {
        var (token, _, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, "?from=2026-10-02&to=2026-10-02");

        Assert.Equal(2, Ids(page).Length);
    }

    [Theory]
    [InlineData(true, new[] { "b-y-4-transfer" })]
    [InlineData(false, new[] { "a-none-3", "b-x-2", "a-y-2", "a-x-1" })]
    public async Task Filtering_by_the_transfer_flag(bool isTransfer, string[] expected)
    {
        var (token, _, _, _, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, $"?isTransfer={isTransfer.ToString().ToLowerInvariant()}");

        Assert.Equal(expected, Descriptions(page));
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        var (token, a, _, x, _) = await SeedFilterDataAsync();

        var page = await ListAsync(token, $"?accountId={a.Id}&categoryId={x.Id}&from=2026-10-01&to=2026-10-01");

        Assert.Equal(["a-x-1"], Descriptions(page));
    }

    [Fact]
    public async Task Filters_and_pagination_work_together()
    {
        var (token, a, _, _, _) = await SeedFilterDataAsync();

        var first = await ListAsync(token, $"?accountId={a.Id}&limit=2");
        var second = await ListAsync(token, $"?accountId={a.Id}&limit=2&cursor={NextCursor(first)}");

        Assert.Equal(["a-none-3", "a-y-2"], Descriptions(first));
        Assert.Equal(["a-x-1"], Descriptions(second));
        Assert.Null(NextCursor(second));
    }

    [Fact]
    public async Task Filtering_by_someone_elses_account_or_category_finds_nothing_rather_than_failing()
    {
        var (alice, aliceToken) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var bobAccount = await AddAccountAsync(bob);
        var bobCategory = await AddCategoryAsync(bob, "Bobs");
        await AddAsync(alice, aliceAccount, "2026-10-01", "alice");
        await AddAsync(bob, bobAccount, "2026-10-01", "bob", bobCategory);

        var byAccount = await ListAsync(aliceToken, $"?accountId={bobAccount.Id}");
        var byCategory = await ListAsync(aliceToken, $"?categoryId={bobCategory.Id}");

        Assert.Empty(Ids(byAccount));
        Assert.Empty(Ids(byCategory));
    }
}
