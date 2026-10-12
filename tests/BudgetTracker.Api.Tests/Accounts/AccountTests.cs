using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Accounts;

public class AccountTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<(User User, string Token)> NewUserAsync() => await CreateUserWithSessionAsync();

    private async Task<Guid> CreateAsync(string token, string name, string type = "Checking", bool? invertSign = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/accounts", token, new { name, type, invertSign });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static string[] Names(JsonElement array) =>
        [.. array.EnumerateArray().Select(a => a.GetProperty("name").GetString()!)];

    private static string ErrorCode(JsonElement problem, string field) =>
        problem.GetProperty("codes").GetProperty(field)[0].GetString()!;

    // Create

    [Fact]
    public async Task Creating_an_account_returns_201_with_a_location_and_the_full_resource()
    {
        var (user, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/accounts", token,
            new { name = "Everyday", type = "Checking", invertSign = false });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/accounts/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Everyday", body.GetProperty("name").GetString());
        Assert.Equal("Checking", body.GetProperty("type").GetString());
        Assert.False(body.GetProperty("invertSign").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("archivedAt").ValueKind);
        Assert.Equal(
            ["archivedAt", "id", "invertSign", "name", "type"],
            body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(user.Id, (await Db.Accounts.AsNoTracking().SingleAsync()).UserId);
    }

    [Fact]
    public async Task The_sign_setting_defaults_to_false_and_can_be_set()
    {
        var (_, token) = await NewUserAsync();

        await CreateAsync(token, "Plain");
        await CreateAsync(token, "Card", "Credit", invertSign: true);

        var stored = await Db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        Assert.Equal([("Card", true), ("Plain", false)], stored.Select(a => (a.Name, a.InvertSign)).ToArray());
    }

    [Theory]
    [InlineData("Checking")]
    [InlineData("Savings")]
    [InlineData("Credit")]
    [InlineData("  Savings  ")]
    public async Task Every_account_type_is_accepted(string type)
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/accounts", token, new { name = "A", type });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "type.required")]
    [InlineData("", "type.required")]
    [InlineData("   ", "type.required")]
    [InlineData("checking", "type.invalid")]
    [InlineData("CHECKING", "type.invalid")]
    [InlineData("Brokerage", "type.invalid")]
    [InlineData("1", "type.invalid")]
    [InlineData("Checking, Savings", "type.invalid")]
    [InlineData("Checking|Savings", "type.invalid")]
    public async Task An_unusable_type_is_a_400_naming_the_problem(string? type, string code)
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/accounts", token, new { name = "A", type });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, ErrorCode(await ReadJsonAsync(response), "type"));
        Assert.Equal(0, await Db.Accounts.CountAsync());
    }

    [Fact]
    public async Task A_numeric_type_is_a_400_not_a_silent_enum_cast()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/accounts", token, new { name = "A", type = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Name_and_type_problems_are_reported_together()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/accounts", token, new { name = " ", type = "Nope" });

        var problem = await ReadJsonAsync(response);
        Assert.Equal("name.required", ErrorCode(problem, "name"));
        Assert.Equal("type.invalid", ErrorCode(problem, "type"));
    }

    [Fact]
    public async Task The_name_is_trimmed_and_limited_to_one_hundred_characters()
    {
        var (_, token) = await NewUserAsync();

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/accounts", token, new { name = "  Padded  ", type = "Checking" })).StatusCode);
        Assert.Equal("Padded", (await Db.Accounts.AsNoTracking().SingleAsync()).Name);

        var tooLong = await SendAsync(HttpMethod.Post, "/accounts", token, new { name = new string('x', 101), type = "Checking" });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("name.too_long", ErrorCode(await ReadJsonAsync(tooLong), "name"));
    }

    [Fact]
    public async Task A_duplicate_name_is_a_409_for_the_same_user_but_fine_for_another()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        await CreateAsync(alice, "Everyday");

        var duplicate = await SendAsync(HttpMethod.Post, "/accounts", alice, new { name = "Everyday", type = "Savings" });
        var otherUser = await SendAsync(HttpMethod.Post, "/accounts", bob, new { name = "Everyday", type = "Checking" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("account.name_taken", (await ReadJsonAsync(duplicate)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, otherUser.StatusCode);
    }

    [Fact]
    public async Task Two_simultaneous_creates_of_one_name_make_exactly_one_account()
    {
        var (user, token) = await NewUserAsync();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            SendAsync(HttpMethod.Post, "/accounts", token, new { name = "Race", type = "Checking" })));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Db.Accounts.CountAsync(a => a.UserId == user.Id));
    }

    // Read

    [Fact]
    public async Task The_list_is_ordered_by_name_and_contains_only_the_callers_accounts()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        await CreateAsync(alice, "Cherry");
        await CreateAsync(alice, "Apple");
        await CreateAsync(alice, "Banana");
        await CreateAsync(bob, "Bobs private account");

        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts", alice));

        Assert.Equal(["Apple", "Banana", "Cherry"], Names(list));
    }

    [Fact]
    public async Task Archived_accounts_are_hidden_from_the_list_unless_asked_for()
    {
        var (_, token) = await NewUserAsync();
        await CreateAsync(token, "Open");
        var closed = await CreateAsync(token, "Closed");
        await SendAsync(HttpMethod.Delete, $"/accounts/{closed}", token);

        var byDefault = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts", token));
        var withArchived = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts?includeArchived=true", token));
        var explicitlyHidden = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts?includeArchived=false", token));

        Assert.Equal(["Open"], Names(byDefault));
        Assert.Equal(["Closed", "Open"], Names(withArchived));
        Assert.Equal(["Open"], Names(explicitlyHidden));
    }

    [Fact]
    public async Task An_archived_account_can_still_be_fetched_by_id_and_shows_when_it_was_archived()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Closed");
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);

        var response = await SendAsync(HttpMethod.Get, $"/accounts/{id}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Clock.GetUtcNow(), (await ReadJsonAsync(response)).GetProperty("archivedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Another_users_account_looks_exactly_like_one_that_does_not_exist()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var someoneElses = await SendAsync(HttpMethod.Get, $"/accounts/{bobsId}", alice);
        var missing = await SendAsync(HttpMethod.Get, $"/accounts/{Guid.NewGuid()}", alice);

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(someoneElses)).GetProperty("code").GetString());
    }

    // Update

    [Fact]
    public async Task Putting_replaces_the_name_type_and_sign_setting()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday", "Checking", invertSign: false);

        var response = await SendAsync(HttpMethod.Put, $"/accounts/{id}", token,
            new { name = "Visa", type = "Credit", invertSign = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Db.Accounts.AsNoTracking().SingleAsync();
        Assert.Equal(("Visa", AccountType.Credit, true), (stored.Name, stored.Type, stored.InvertSign));
    }

    [Fact]
    public async Task Putting_without_the_sign_setting_resets_it_because_put_replaces_the_whole_resource()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Visa", "Credit", invertSign: true);

        await SendAsync(HttpMethod.Put, $"/accounts/{id}", token, new { name = "Visa", type = "Credit" });

        Assert.False((await Db.Accounts.AsNoTracking().SingleAsync()).InvertSign);
    }

    [Fact]
    public async Task Put_cannot_archive_or_unarchive_an_account()
    {
        var (_, token) = await NewUserAsync();
        var open = await CreateAsync(token, "Open");
        var closed = await CreateAsync(token, "Closed");
        await SendAsync(HttpMethod.Delete, $"/accounts/{closed}", token);

        await SendAsync(HttpMethod.Put, $"/accounts/{open}", token,
            new { name = "Open", type = "Checking", archivedAt = "2020-01-01T00:00:00Z" });
        await SendAsync(HttpMethod.Put, $"/accounts/{closed}", token,
            new { name = "Closed", type = "Checking", archivedAt = (string?)null });

        Assert.Null((await Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == open)).ArchivedAt);
        Assert.NotNull((await Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == closed)).ArchivedAt);
    }

    [Fact]
    public async Task An_archived_account_can_be_renamed()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Old name");
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);

        var response = await SendAsync(HttpMethod.Put, $"/accounts/{id}", token, new { name = "Tidier name", type = "Checking" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task Renaming_to_a_name_you_already_use_is_a_409_and_changes_nothing()
    {
        var (_, token) = await NewUserAsync();
        await CreateAsync(token, "Taken");
        var id = await CreateAsync(token, "Mine");

        var response = await SendAsync(HttpMethod.Put, $"/accounts/{id}", token, new { name = "Taken", type = "Savings" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = await Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(("Mine", AccountType.Checking), (stored.Name, stored.Type));
    }

    [Fact]
    public async Task An_invalid_put_is_a_400_and_changes_nothing()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Mine");

        var response = await SendAsync(HttpMethod.Put, $"/accounts/{id}", token, new { name = "", type = "Nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Mine", (await Db.Accounts.AsNoTracking().SingleAsync()).Name);
    }

    [Fact]
    public async Task Another_users_account_cannot_be_replaced()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var response = await SendAsync(HttpMethod.Put, $"/accounts/{bobsId}", alice, new { name = "Hijacked", type = "Credit" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Bobs", (await Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bobsId)).Name);
    }

    // Archive and unarchive

    [Fact]
    public async Task Delete_archives_the_account_and_keeps_its_transactions()
    {
        var (user, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");
        Db.Add(new Transaction
        {
            UserId = user.Id, AccountId = id, PostedOn = new DateOnly(2026, 10, 1), Amount = -5m, Description = "coffee",
        });
        await Db.SaveChangesAsync();

        var response = await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Clock.GetUtcNow(), (await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
        Assert.Equal(1, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Archiving_twice_keeps_the_original_time()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);
        var firstTime = Clock.GetUtcNow();

        Clock.Advance(TimeSpan.FromDays(3));
        var again = await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(firstTime, (await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task Permanent_false_is_the_same_as_leaving_it_out()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");

        var response = await SendAsync(HttpMethod.Delete, $"/accounts/{id}?permanent=false", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull((await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task Unarchiving_reopens_an_archived_account()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);

        var response = await SendAsync(HttpMethod.Post, $"/accounts/{id}/unarchive", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadJsonAsync(response)).GetProperty("archivedAt").ValueKind);
        Assert.Null((await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
        Assert.Equal(["Everyday"], Names(await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts", token))));
    }

    [Fact]
    public async Task Unarchiving_an_open_account_changes_nothing()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");

        var response = await SendAsync(HttpMethod.Post, $"/accounts/{id}/unarchive", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_account_cannot_be_archived_or_unarchived()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");

        var archive = await SendAsync(HttpMethod.Delete, $"/accounts/{bobsId}", alice);
        var unarchive = await SendAsync(HttpMethod.Post, $"/accounts/{bobsId}/unarchive", alice);

        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unarchive.StatusCode);
        Assert.Null((await Db.Accounts.AsNoTracking().SingleAsync(a => a.Id == bobsId)).ArchivedAt);
    }

    // Permanent deletion

    [Fact]
    public async Task Permanent_deletion_of_an_open_account_is_refused_and_deletes_nothing()
    {
        var (user, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");
        Db.Add(new Transaction
        {
            UserId = user.Id, AccountId = id, PostedOn = new DateOnly(2026, 10, 1), Amount = -5m, Description = "coffee",
        });
        await Db.SaveChangesAsync();

        var response = await SendAsync(HttpMethod.Delete, $"/accounts/{id}?permanent=true", token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("account.not_archived", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, await Db.Accounts.CountAsync());
        Assert.Equal(1, await Db.Transactions.CountAsync());
        Assert.Null((await Db.Accounts.AsNoTracking().SingleAsync()).ArchivedAt);
    }

    [Fact]
    public async Task An_archived_account_can_be_deleted_permanently_with_its_transactions_and_nothing_else()
    {
        var (user, token) = await NewUserAsync();
        var doomed = await CreateAsync(token, "Doomed");
        var kept = await CreateAsync(token, "Kept");
        foreach (var accountId in new[] { doomed, kept })
        {
            Db.Add(new Transaction
            {
                UserId = user.Id, AccountId = accountId, PostedOn = new DateOnly(2026, 10, 1), Amount = -5m, Description = "x",
            });
        }

        await Db.SaveChangesAsync();
        await SendAsync(HttpMethod.Delete, $"/accounts/{doomed}", token);

        var response = await SendAsync(HttpMethod.Delete, $"/accounts/{doomed}?permanent=true", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([kept], await Db.Accounts.Select(a => a.Id).ToListAsync());
        Assert.Equal([kept], await Db.Transactions.Select(t => t.AccountId).ToListAsync());
    }

    [Fact]
    public async Task Archive_then_permanent_delete_is_the_only_way_and_a_second_permanent_delete_is_a_404()
    {
        var (_, token) = await NewUserAsync();
        var id = await CreateAsync(token, "Everyday");
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}", token);
        await SendAsync(HttpMethod.Delete, $"/accounts/{id}?permanent=true", token);

        var again = await SendAsync(HttpMethod.Delete, $"/accounts/{id}?permanent=true", token);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Another_users_account_cannot_be_deleted_permanently()
    {
        var (_, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var bobsId = await CreateAsync(bob, "Bobs");
        await SendAsync(HttpMethod.Delete, $"/accounts/{bobsId}", bob);

        var response = await SendAsync(HttpMethod.Delete, $"/accounts/{bobsId}?permanent=true", alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await Db.Accounts.CountAsync(a => a.UserId == bobUser.Id));
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/accounts")]
    [InlineData("POST", "/accounts")]
    [InlineData("GET", "/accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/accounts/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/accounts/00000000-0000-0000-0000-000000000001/unarchive")]
    public async Task Every_endpoint_requires_a_session(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, token: null, body: new { name = "x", type = "Checking" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
