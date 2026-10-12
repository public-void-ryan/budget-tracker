using System.Net;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Categories;

public class CategoryTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<(User User, string Token)> NewUserAsync() => await CreateUserWithSessionAsync();

    private async Task<Guid> CreateAsync(string token, string name)
    {
        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static string[] Names(System.Text.Json.JsonElement array) =>
        [.. array.EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    // Create

    [Fact]
    public async Task Creating_a_category_returns_201_with_a_location_and_stores_it_for_the_caller()
    {
        var (user, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { name = "Groceries" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/categories/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Groceries", body.GetProperty("name").GetString());
        Assert.Equal(["id", "name"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        var stored = await Db.Categories.AsNoTracking().SingleAsync();
        Assert.Equal((id, user.Id, "Groceries"), (stored.Id, stored.UserId, stored.Name));
    }

    [Fact]
    public async Task The_name_is_trimmed()
    {
        var (_, token) = await NewUserAsync();

        await SendAsync(HttpMethod.Post, "/categories", token, new { name = "  Groceries  " });

        Assert.Equal("Groceries", (await Db.Categories.AsNoTracking().SingleAsync()).Name);
    }

    [Theory]
    [InlineData(null, "name.required")]
    [InlineData("", "name.required")]
    [InlineData("   ", "name.required")]
    [InlineData("two\nlines", "name.invalid")]
    [InlineData("tab\there", "name.invalid")]
    public async Task An_unusable_name_is_a_400_naming_the_problem(string? name, string code)
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("codes").GetProperty("name")[0].GetString());
        Assert.Equal(0, await Db.Categories.CountAsync());
    }

    [Fact]
    public async Task A_missing_body_field_is_reported_the_same_as_an_empty_name()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_name_may_be_exactly_one_hundred_characters_and_not_one_more()
    {
        var (_, token) = await NewUserAsync();

        Assert.Equal(
            HttpStatusCode.Created,
            (await SendAsync(HttpMethod.Post, "/categories", token, new { name = new string('a', 100) })).StatusCode);
        var tooLong = await SendAsync(HttpMethod.Post, "/categories", token, new { name = new string('b', 101) });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("name.too_long", (await ReadJsonAsync(tooLong)).GetProperty("codes").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task Length_counts_characters_so_a_hundred_emoji_fit_the_column()
    {
        // 100 emoji are 200 UTF-16 units; counting those would wrongly reject a name the
        // varchar(100) column accepts.
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { name = string.Concat(Enumerable.Repeat("😀", 100)) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_name_is_a_409_for_the_same_user_but_fine_for_another()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        await CreateAsync(alice, "Groceries");

        var duplicate = await SendAsync(HttpMethod.Post, "/categories", alice, new { name = "Groceries" });
        var otherUser = await SendAsync(HttpMethod.Post, "/categories", bob, new { name = "Groceries" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("category.name_taken", (await ReadJsonAsync(duplicate)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, otherUser.StatusCode);
    }

    [Fact]
    public async Task Two_simultaneous_creates_of_one_name_make_exactly_one_category()
    {
        var (user, token) = await NewUserAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            SendAsync(HttpMethod.Post, "/categories", token, new { name = "Race" })));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Db.Categories.CountAsync(c => c.UserId == user.Id));
    }

    // Read

    [Fact]
    public async Task The_list_is_empty_at_first()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Get, "/categories", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ReadJsonAsync(response)).EnumerateArray());
    }

    [Fact]
    public async Task The_list_is_ordered_by_name_and_contains_only_the_callers_categories()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        await CreateAsync(alice, "Cherries");
        await CreateAsync(alice, "Apples");
        await CreateAsync(alice, "Bananas");
        await CreateAsync(bob, "Bobs private category");

        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/categories", alice));

        Assert.Equal(["Apples", "Bananas", "Cherries"], Names(list));
    }

    [Fact]
    public async Task One_category_can_be_fetched_by_id()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Get, $"/categories/{id}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Groceries", (await ReadJsonAsync(response)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Another_users_category_looks_exactly_like_one_that_does_not_exist()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var someoneElses = await SendAsync(HttpMethod.Get, $"/categories/{bobsId}", alice);
        var missing = await SendAsync(HttpMethod.Get, $"/categories/{Guid.NewGuid()}", alice);

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(someoneElses)).GetProperty("code").GetString());
        Assert.Equal("not_found", (await ReadJsonAsync(missing)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_is_a_404()
    {
        var (_, token) = await NewUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/categories/not-a-guid", token)).StatusCode);
    }

    // Update

    [Fact]
    public async Task Renaming_replaces_the_name()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Put, $"/categories/{id}", token, new { name = "  Food  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Food", (await ReadJsonAsync(response)).GetProperty("name").GetString());
        Assert.Equal("Food", (await Db.Categories.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Putting_the_same_name_back_is_fine()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Put, $"/categories/{id}", token, new { name = "Groceries" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Renaming_to_a_name_you_already_use_is_a_409_and_changes_nothing()
    {
        var (_, token) = await NewUserAsync();
        await CreateAsync(token, "Food");
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Put, $"/categories/{id}", token, new { name = "Food" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("category.name_taken", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal("Groceries", (await Db.Categories.AsNoTracking().SingleAsync(c => c.Id == id)).Name);
    }

    [Fact]
    public async Task Renaming_with_an_unusable_name_is_a_400()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Put, $"/categories/{id}", token, new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Groceries", (await Db.Categories.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Another_users_category_cannot_be_renamed()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var response = await SendAsync(HttpMethod.Put, $"/categories/{bobsId}", alice, new { name = "Hijacked" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Bobs", (await Db.Categories.AsNoTracking().SingleAsync(c => c.Id == bobsId)).Name);
    }

    [Fact]
    public async Task Renaming_a_category_that_does_not_exist_is_a_404()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Put, $"/categories/{Guid.NewGuid()}", token, new { name = "Anything" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Delete

    [Fact]
    public async Task Deleting_removes_the_category()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        var response = await SendAsync(HttpMethod.Delete, $"/categories/{id}", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await Db.Categories.CountAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/categories/{id}", token)).StatusCode);
    }

    [Fact]
    public async Task Deleting_keeps_the_transactions_uncategorized_and_removes_the_budgets()
    {
        var (user, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");
        var account = new Account { UserId = user.Id, Name = "Checking" };
        Db.Add(account);
        await Db.SaveChangesAsync();
        var transaction = new Transaction
        {
            UserId = user.Id, AccountId = account.Id, CategoryId = id,
            PostedOn = new DateOnly(2026, 10, 1), Amount = -12.5m, Description = "milk",
        };
        Db.Add(transaction);
        Db.Add(new Budget { UserId = user.Id, CategoryId = id, Month = new DateOnly(2026, 10, 1), Amount = 300m });
        await Db.SaveChangesAsync();

        await SendAsync(HttpMethod.Delete, $"/categories/{id}", token);

        var kept = await Db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id);
        Assert.Null(kept.CategoryId);
        Assert.Equal(user.Id, kept.UserId);
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task Another_users_category_cannot_be_deleted()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var response = await SendAsync(HttpMethod.Delete, $"/categories/{bobsId}", alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await Db.Categories.CountAsync(c => c.Id == bobsId));
    }

    [Fact]
    public async Task Deleting_twice_is_a_404_the_second_time()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");

        await SendAsync(HttpMethod.Delete, $"/categories/{id}", token);
        var again = await SendAsync(HttpMethod.Delete, $"/categories/{id}", token);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_deleted_name_can_be_used_again()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Groceries");
        await SendAsync(HttpMethod.Delete, $"/categories/{id}", token);

        var response = await SendAsync(HttpMethod.Post, "/categories", token, new { name = "Groceries" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/categories")]
    [InlineData("POST", "/categories")]
    [InlineData("GET", "/categories/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/categories/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/categories/00000000-0000-0000-0000-000000000001")]
    public async Task Every_endpoint_requires_a_session(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, token: null, body: new { name = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
