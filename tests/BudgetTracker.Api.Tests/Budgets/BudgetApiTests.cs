using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Budgets;

public class BudgetApiTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<Category> AddCategoryAsync(User user, string name)
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    private Task<HttpResponseMessage> PutAsync(string token, string month, Guid categoryId, object? body) =>
        SendAsync(HttpMethod.Put, $"/budgets/{month}/{categoryId}", token, body);

    private static string Code(JsonElement problem, string field) =>
        problem.GetProperty("codes").GetProperty(field)[0].GetString()!;

    private async Task<JsonElement> ListAsync(string token, string month)
    {
        var response = await SendAsync(HttpMethod.Get, $"/budgets?month={month}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static (string, decimal)[] Names(JsonElement array) =>
        [.. array.EnumerateArray().Select(b => (b.GetProperty("categoryName").GetString()!, b.GetProperty("amount").GetDecimal()))];

    // Setting a budget

    [Fact]
    public async Task Setting_a_budget_creates_it_with_a_201_and_the_resource()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var groceries = await AddCategoryAsync(user, "Groceries");

        var response = await PutAsync(token, "2026-10", groceries.Id, new { amount = 400.50m });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/budgets?month=2026-10", response.Headers.Location?.OriginalString);
        var body = await ReadJsonAsync(response);
        Assert.Equal(
            ["amount", "categoryId", "categoryName", "month"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("Groceries", "2026-10", 400.50m),
            (body.GetProperty("categoryName").GetString(), body.GetProperty("month").GetString(), body.GetProperty("amount").GetDecimal()));
        var stored = await Db.Budgets.AsNoTracking().SingleAsync();
        Assert.Equal((user.Id, groceries.Id, new DateOnly(2026, 10, 1), 400.50m), (stored.UserId, stored.CategoryId, stored.Month, stored.Amount));
    }

    [Fact]
    public async Task Setting_it_again_replaces_the_amount_with_a_200_and_never_adds_a_second_row()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var groceries = await AddCategoryAsync(user, "Groceries");
        await PutAsync(token, "2026-10", groceries.Id, new { amount = 400m });

        var again = await PutAsync(token, "2026-10", groceries.Id, new { amount = 450m });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(450m, (await Db.Budgets.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task The_same_category_can_have_a_budget_in_every_month()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var groceries = await AddCategoryAsync(user, "Groceries");

        await PutAsync(token, "2026-09", groceries.Id, new { amount = 300m });
        await PutAsync(token, "2026-10", groceries.Id, new { amount = 400m });

        Assert.Equal(2, await Db.Budgets.CountAsync());
    }

    [Theory]
    [InlineData("0", HttpStatusCode.Created)]
    [InlineData("0.0001", HttpStatusCode.Created)]
    [InlineData("999999999999999.9999", HttpStatusCode.Created)]
    [InlineData("12.50000", HttpStatusCode.Created)]
    public async Task Valid_amounts_including_zero_are_stored_exactly(string amount, HttpStatusCode expected)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var category = await AddCategoryAsync(user, "Anything");

        var response = await PutAsync(token, "2026-10", category.Id, new { amount = decimal.Parse(amount) });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(decimal.Parse(amount), (await Db.Budgets.AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData("-0.01", "amount.negative")]
    [InlineData("-100", "amount.negative")]
    [InlineData("12.34567", "amount.too_precise")]
    [InlineData("1000000000000000", "amount.out_of_range")]
    public async Task An_unusable_amount_is_a_400_and_stores_nothing(string amount, string code)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var category = await AddCategoryAsync(user, "Anything");

        var response = await PutAsync(token, "2026-10", category.Id, new { amount = decimal.Parse(amount) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "amount"));
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task A_missing_or_string_amount_is_refused()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var category = await AddCategoryAsync(user, "Anything");

        var missing = await PutAsync(token, "2026-10", category.Id, new { });
        var text = await PutAsync(token, "2026-10", category.Id, new { amount = "400" });

        Assert.Equal("amount.required", Code(await ReadJsonAsync(missing), "amount"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("202610")]
    [InlineData("2026-10-01")]
    [InlineData("october")]
    public async Task A_month_that_is_not_yyyy_MM_is_a_400(string month)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var category = await AddCategoryAsync(user, "Anything");

        var response = await PutAsync(token, month, category.Id, new { amount = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("month.invalid", Code(await ReadJsonAsync(response), "month"));
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task A_category_that_is_missing_or_belongs_to_someone_else_is_a_404()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");

        var foreign = await PutAsync(alice, "2026-10", bobsCategory.Id, new { amount = 100m });
        var missing = await PutAsync(alice, "2026-10", Guid.NewGuid(), new { amount = 100m });

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(foreign)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task Simultaneous_requests_for_the_same_budget_never_collide()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var category = await AddCategoryAsync(user, "Race");

        var responses = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            PutAsync(token, "2026-10", category.Id, new { amount = i * 10m })));

        Assert.All(responses, r => Assert.True(
            r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"got {(int)r.StatusCode}"));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var stored = await Db.Budgets.AsNoTracking().SingleAsync();
        Assert.Contains(stored.Amount, Enumerable.Range(1, 8).Select(i => i * 10m));
    }

    // Listing

    [Fact]
    public async Task The_list_is_one_month_ordered_by_category_name_and_only_the_callers()
    {
        var (aliceUser, alice) = await CreateUserWithSessionAsync();
        var (bobUser, bob) = await CreateUserWithSessionAsync();
        var rent = await AddCategoryAsync(aliceUser, "Rent");
        var dining = await AddCategoryAsync(aliceUser, "Dining");
        var bobs = await AddCategoryAsync(bobUser, "Bobs");
        await PutAsync(alice, "2026-10", rent.Id, new { amount = 1000m });
        await PutAsync(alice, "2026-10", dining.Id, new { amount = 200m });
        await PutAsync(alice, "2026-09", rent.Id, new { amount = 900m });
        await PutAsync(bob, "2026-10", bobs.Id, new { amount = 50m });

        var october = await ListAsync(alice, "2026-10");

        Assert.Equal([("Dining", 200m), ("Rent", 1000m)], Names(october));
        Assert.Empty((await ListAsync(alice, "2026-11")).EnumerateArray());
    }

    [Theory]
    [InlineData("", "month.required")]
    [InlineData("?month=", "month.required")]
    [InlineData("?month=2026-1", "month.invalid")]
    public async Task Listing_needs_a_valid_month(string query, string code)
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var response = await SendAsync(HttpMethod.Get, "/budgets" + query, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "month"));
    }

    [Fact]
    public async Task Deleting_a_category_removes_its_budgets_from_the_list()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var doomed = await AddCategoryAsync(user, "Doomed");
        await PutAsync(token, "2026-10", doomed.Id, new { amount = 100m });

        await SendAsync(HttpMethod.Delete, $"/categories/{doomed.Id}", token);

        Assert.Empty((await ListAsync(token, "2026-10")).EnumerateArray());
    }

    // Deleting

    [Fact]
    public async Task Deleting_removes_one_months_budget_only_and_a_second_delete_is_a_404()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var rent = await AddCategoryAsync(user, "Rent");
        await PutAsync(token, "2026-09", rent.Id, new { amount = 900m });
        await PutAsync(token, "2026-10", rent.Id, new { amount = 1000m });

        var first = await SendAsync(HttpMethod.Delete, $"/budgets/2026-10/{rent.Id}", token);
        var second = await SendAsync(HttpMethod.Delete, $"/budgets/2026-10/{rent.Id}", token);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(new DateOnly(2026, 9, 1), (await Db.Budgets.AsNoTracking().SingleAsync()).Month);
    }

    [Fact]
    public async Task Another_users_budget_cannot_be_deleted_and_a_bad_month_is_a_400()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bobUser, bob) = await CreateUserWithSessionAsync();
        var bobsCategory = await AddCategoryAsync(bobUser, "Bobs");
        await PutAsync(bob, "2026-10", bobsCategory.Id, new { amount = 50m });

        var foreign = await SendAsync(HttpMethod.Delete, $"/budgets/2026-10/{bobsCategory.Id}", alice);
        var badMonth = await SendAsync(HttpMethod.Delete, $"/budgets/2026-1/{bobsCategory.Id}", alice);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badMonth.StatusCode);
        Assert.Equal(1, await Db.Budgets.CountAsync());
    }

    // Copying

    private async Task<(User User, string Token, Category Rent, Category Dining, Category Fun)> SeedMonthAsync()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var rent = await AddCategoryAsync(user, "Rent");
        var dining = await AddCategoryAsync(user, "Dining");
        var fun = await AddCategoryAsync(user, "Fun");
        await PutAsync(token, "2026-09", rent.Id, new { amount = 1000m });
        await PutAsync(token, "2026-09", dining.Id, new { amount = 200m });
        await PutAsync(token, "2026-09", fun.Id, new { amount = 0m });
        return (user, token, rent, dining, fun);
    }

    [Fact]
    public async Task Copying_adds_every_budget_the_target_month_does_not_have()
    {
        var (_, token, _, _, _) = await SeedMonthAsync();

        var response = await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal((3, 0), (body.GetProperty("copied").GetInt32(), body.GetProperty("skippedExisting").GetInt32()));
        Assert.Equal([("Dining", 200m), ("Fun", 0m), ("Rent", 1000m)], Names(await ListAsync(token, "2026-10")));
    }

    [Fact]
    public async Task Copying_never_overwrites_a_budget_that_already_exists()
    {
        var (_, token, rent, _, _) = await SeedMonthAsync();
        await PutAsync(token, "2026-10", rent.Id, new { amount = 1234m });

        var body = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", token));

        Assert.Equal((2, 1), (body.GetProperty("copied").GetInt32(), body.GetProperty("skippedExisting").GetInt32()));
        var october = Names(await ListAsync(token, "2026-10"));
        Assert.Contains(("Rent", 1234m), october);
    }

    [Fact]
    public async Task Copying_is_safe_to_repeat_and_leaves_the_source_alone()
    {
        var (_, token, _, _, _) = await SeedMonthAsync();
        await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", token);

        var again = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", token));

        Assert.Equal((0, 3), (again.GetProperty("copied").GetInt32(), again.GetProperty("skippedExisting").GetInt32()));
        Assert.Equal(6, await Db.Budgets.CountAsync());
        Assert.Equal(3, (await ListAsync(token, "2026-09")).GetArrayLength());
    }

    [Fact]
    public async Task Copying_from_an_empty_month_copies_nothing()
    {
        var (_, token, _, _, _) = await SeedMonthAsync();

        var body = await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-01&to=2026-02", token));

        Assert.Equal((0, 0), (body.GetProperty("copied").GetInt32(), body.GetProperty("skippedExisting").GetInt32()));
    }

    [Fact]
    public async Task Copying_never_touches_another_users_budgets()
    {
        var (_, alice, _, _, _) = await SeedMonthAsync();
        var (bobUser, bob) = await CreateUserWithSessionAsync();
        var bobsCategory = await AddCategoryAsync(bobUser, "Bobs");
        await PutAsync(bob, "2026-09", bobsCategory.Id, new { amount = 50m });

        await SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", alice);

        Assert.Empty((await ListAsync(bob, "2026-10")).EnumerateArray());
        Assert.Equal(3, (await ListAsync(alice, "2026-10")).GetArrayLength());
    }

    [Theory]
    [InlineData("?from=2026-09&to=2026-09", "to", "to.same_as_from")]
    [InlineData("?to=2026-10", "from", "from.required")]
    [InlineData("?from=2026-09", "to", "to.required")]
    [InlineData("?from=2026-9&to=2026-10", "from", "from.invalid")]
    [InlineData("?from=2026-09&to=2026-1", "to", "to.invalid")]
    public async Task Bad_copy_parameters_are_a_400_naming_the_parameter(string query, string field, string code)
    {
        var (_, token, _, _, _) = await SeedMonthAsync();

        var response = await SendAsync(HttpMethod.Post, "/budgets/copy" + query, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), field));
        Assert.Equal(3, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task Simultaneous_copies_into_the_same_month_add_each_budget_once_and_all_succeed()
    {
        var (_, token, _, _, _) = await SeedMonthAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            SendAsync(HttpMethod.Post, "/budgets/copy?from=2026-09&to=2026-10", token)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var copied = 0;
        foreach (var response in responses)
        {
            copied += (await ReadJsonAsync(response)).GetProperty("copied").GetInt32();
        }

        Assert.Equal(3, copied);
        Assert.Equal(6, await Db.Budgets.CountAsync());
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/budgets?month=2026-10")]
    [InlineData("PUT", "/budgets/2026-10/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/budgets/2026-10/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/budgets/copy?from=2026-09&to=2026-10")]
    public async Task Every_endpoint_requires_a_session(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, token: null, body: new { amount = 1m });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
