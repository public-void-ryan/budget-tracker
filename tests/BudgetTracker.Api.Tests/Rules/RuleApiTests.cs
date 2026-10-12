using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Rules;

public class RuleApiTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<(User User, string Token)> NewUserAsync() => await CreateUserWithSessionAsync();

    private async Task<Category> AddCategoryAsync(User user, string name = "Groceries")
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    private async Task<Guid> CreateAsync(
        string token, string pattern, string matchType = "Contains", Guid? categoryId = null, bool? markTransfer = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern, matchType, categoryId, markTransfer });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateWithCategoryAsync(User user, string token, string pattern, string matchType = "Contains")
    {
        var category = await AddCategoryAsync(user, $"cat-{Guid.NewGuid():N}");
        return await CreateAsync(token, pattern, matchType, category.Id);
    }

    private static string[] Patterns(JsonElement array) =>
        [.. array.EnumerateArray().Select(r => r.GetProperty("pattern").GetString()!)];

    private static string Code(JsonElement problem, string field) =>
        problem.GetProperty("codes").GetProperty(field)[0].GetString()!;

    private async Task<string[]> ListedPatternsAsync(string token) =>
        Patterns(await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules", token)));

    // Create

    [Fact]
    public async Task Creating_a_rule_returns_201_with_a_location_and_the_full_resource()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/rules", token,
            new { pattern = "starbucks", matchType = "StartsWith", categoryId = category.Id, markTransfer = false });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/rules/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(
            ["categoryId", "id", "markTransfer", "matchType", "pattern", "position"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("StartsWith", body.GetProperty("matchType").GetString());
        Assert.Equal(0, body.GetProperty("position").GetInt32());
        var stored = await Db.Rules.AsNoTracking().SingleAsync();
        Assert.Equal((id, user.Id, category.Id, "starbucks"), (stored.Id, stored.UserId, stored.CategoryId, stored.Pattern));
    }

    [Fact]
    public async Task New_rules_go_to_the_end_with_consecutive_positions()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        foreach (var pattern in new[] { "a", "b", "c" })
        {
            await CreateAsync(token, pattern, categoryId: category.Id);
        }

        Assert.Equal(["a", "b", "c"], await ListedPatternsAsync(token));
        Assert.Equal([0, 1, 2], await Db.Rules.AsNoTracking().OrderBy(r => r.Position).Select(r => r.Position).ToListAsync());
    }

    [Fact]
    public async Task A_rule_may_only_mark_transfers_or_do_both()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        await CreateAsync(token, "payment thank you", markTransfer: true);
        await CreateAsync(token, "cc payment", categoryId: category.Id, markTransfer: true);

        var stored = await Db.Rules.AsNoTracking().OrderBy(r => r.Position).ToListAsync();
        Assert.Equal([(null, true), (category.Id, true)], stored.Select(r => (r.CategoryId, r.MarkTransfer)).ToArray());
    }

    [Fact]
    public async Task The_pattern_is_trimmed_and_may_be_two_hundred_characters_and_not_more()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);
        await CreateAsync(token, "  padded  ", categoryId: category.Id);

        var ok = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern = new string('p', 200), matchType = "Contains", categoryId = category.Id });
        var tooLong = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern = new string('p', 201), matchType = "Contains", categoryId = category.Id });

        Assert.Equal("padded", (await Db.Rules.AsNoTracking().OrderBy(r => r.Position).FirstAsync()).Pattern);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("pattern.too_long", Code(await ReadJsonAsync(tooLong), "pattern"));
    }

    [Theory]
    [InlineData(null, "pattern.required")]
    [InlineData("", "pattern.required")]
    [InlineData("   ", "pattern.required")]
    [InlineData("two\nlines", "pattern.invalid")]
    public async Task An_unusable_pattern_is_a_400(string? pattern, string code)
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern, matchType = "Contains", categoryId = category.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "pattern"));
        Assert.Equal(0, await Db.Rules.CountAsync());
    }

    [Theory]
    [InlineData(null, "matchType.required")]
    [InlineData("contains", "matchType.invalid")]
    [InlineData("Regex", "matchType.invalid")]
    [InlineData("Contains, Equals", "matchType.invalid")]
    public async Task An_unusable_match_type_is_a_400(string? matchType, string code)
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern = "x", matchType, categoryId = category.Id });

        Assert.Equal(code, Code(await ReadJsonAsync(response), "matchType"));
    }

    [Fact]
    public async Task A_numeric_match_type_is_a_400_not_a_silent_enum_cast()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern = "x", matchType = 1, categoryId = category.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.Rules.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task A_rule_that_does_nothing_is_a_400(bool? markTransfer)
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/rules", token,
            new { pattern = "x", matchType = "Contains", categoryId = (Guid?)null, markTransfer });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("action.required", Code(await ReadJsonAsync(response), "action"));
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/rules", token, new { pattern = "", matchType = "nope" });

        var problem = await ReadJsonAsync(response);
        Assert.Equal("pattern.required", Code(problem, "pattern"));
        Assert.Equal("matchType.invalid", Code(problem, "matchType"));
        Assert.Equal("action.required", Code(problem, "action"));
    }

    [Fact]
    public async Task A_category_that_is_missing_or_belongs_to_someone_else_is_a_422()
    {
        var (_, alice) = await NewUserAsync();
        var (bob, _) = await NewUserAsync();
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");

        var someoneElses = await SendAsync(HttpMethod.Post, "/rules", alice, new { pattern = "x", matchType = "Contains", categoryId = bobsCategory.Id });
        var missing = await SendAsync(HttpMethod.Post, "/rules", alice, new { pattern = "x", matchType = "Contains", categoryId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("category.invalid", (await ReadJsonAsync(someoneElses)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Rules.CountAsync());
    }

    // Read

    [Fact]
    public async Task The_list_is_empty_at_first_and_then_ordered_with_only_the_callers_rules()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        Assert.Empty((await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules", alice))).EnumerateArray());

        await CreateWithCategoryAsync(aliceUser, alice, "first");
        await CreateWithCategoryAsync(aliceUser, alice, "second");
        await CreateWithCategoryAsync(bobUser, bob, "bobs private rule");

        Assert.Equal(["first", "second"], await ListedPatternsAsync(alice));
    }

    [Fact]
    public async Task One_rule_can_be_fetched_by_id_and_another_users_looks_like_a_missing_one()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var mine = await CreateWithCategoryAsync(aliceUser, alice, "mine");
        var bobs = await CreateWithCategoryAsync(bobUser, bob, "bobs");

        var own = await SendAsync(HttpMethod.Get, $"/rules/{mine}", alice);
        var foreign = await SendAsync(HttpMethod.Get, $"/rules/{bobs}", alice);
        var missing = await SendAsync(HttpMethod.Get, $"/rules/{Guid.NewGuid()}", alice);
        var malformed = await SendAsync(HttpMethod.Get, "/rules/not-a-guid", alice);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(foreign)).GetProperty("code").GetString());
    }

    // Update

    [Fact]
    public async Task Replacing_a_rule_changes_its_fields_but_not_its_place_in_the_order()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user, "Dining");
        await CreateWithCategoryAsync(user, token, "zero");
        var id = await CreateWithCategoryAsync(user, token, "one");
        await CreateWithCategoryAsync(user, token, "two");

        var response = await SendAsync(HttpMethod.Put, $"/rules/{id}", token,
            new { pattern = "  ONE REPLACED ", matchType = "Equals", categoryId = category.Id, markTransfer = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Db.Rules.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal(("ONE REPLACED", RuleMatchType.Equals, category.Id, true, 1),
            (stored.Pattern, stored.MatchType, stored.CategoryId, stored.MarkTransfer, stored.Position));
        Assert.Equal(["zero", "ONE REPLACED", "two"], await ListedPatternsAsync(token));
    }

    [Fact]
    public async Task Putting_without_the_transfer_flag_resets_it_because_put_replaces_everything()
    {
        var (user, token) = await NewUserAsync();
        var category = await AddCategoryAsync(user);
        var id = await CreateAsync(token, "x", categoryId: category.Id, markTransfer: true);

        await SendAsync(HttpMethod.Put, $"/rules/{id}", token, new { pattern = "x", matchType = "Contains", categoryId = category.Id });

        Assert.False((await Db.Rules.AsNoTracking().SingleAsync()).MarkTransfer);
    }

    [Fact]
    public async Task A_refused_put_changes_nothing()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (bob, _) = await NewUserAsync();
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");
        var id = await CreateWithCategoryAsync(aliceUser, alice, "original");
        var originalCategory = (await Db.Rules.AsNoTracking().SingleAsync()).CategoryId;

        var invalid = await SendAsync(HttpMethod.Put, $"/rules/{id}", alice, new { pattern = "", matchType = "Contains", categoryId = originalCategory });
        var noAction = await SendAsync(HttpMethod.Put, $"/rules/{id}", alice, new { pattern = "changed", matchType = "Contains" });
        var foreignCategory = await SendAsync(HttpMethod.Put, $"/rules/{id}", alice, new { pattern = "changed", matchType = "Contains", categoryId = bobsCategory.Id });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noAction.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignCategory.StatusCode);
        var stored = await Db.Rules.AsNoTracking().SingleAsync();
        Assert.Equal(("original", originalCategory), (stored.Pattern, stored.CategoryId));
    }

    [Fact]
    public async Task Another_users_rule_cannot_be_replaced_and_a_missing_one_is_a_404_before_validation()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var bobsRule = await CreateWithCategoryAsync(bobUser, bob, "bobs");
        var bobsCategory = (await Db.Rules.AsNoTracking().SingleAsync()).CategoryId;

        var foreign = await SendAsync(HttpMethod.Put, $"/rules/{bobsRule}", alice, new { pattern = "hijack", matchType = "Contains", markTransfer = true });
        var missingWithBadBody = await SendAsync(HttpMethod.Put, $"/rules/{Guid.NewGuid()}", alice, new { });

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingWithBadBody.StatusCode);
        var stored = await Db.Rules.AsNoTracking().SingleAsync();
        Assert.Equal(("bobs", bobsCategory), (stored.Pattern, stored.CategoryId));
        _ = aliceUser;
    }

    // Delete

    [Fact]
    public async Task Deleting_removes_the_rule_and_keeps_the_order_of_the_rest()
    {
        var (user, token) = await NewUserAsync();
        await CreateWithCategoryAsync(user, token, "a");
        var middle = await CreateWithCategoryAsync(user, token, "b");
        await CreateWithCategoryAsync(user, token, "c");

        var response = await SendAsync(HttpMethod.Delete, $"/rules/{middle}", token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["a", "c"], await ListedPatternsAsync(token));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/rules/{middle}", token)).StatusCode);
    }

    [Fact]
    public async Task A_rule_created_after_a_deletion_still_goes_last()
    {
        var (user, token) = await NewUserAsync();
        await CreateWithCategoryAsync(user, token, "a");
        var second = await CreateWithCategoryAsync(user, token, "b");
        await SendAsync(HttpMethod.Delete, $"/rules/{second}", token);

        await CreateWithCategoryAsync(user, token, "c");

        Assert.Equal(["a", "c"], await ListedPatternsAsync(token));
    }

    [Fact]
    public async Task Another_users_rule_cannot_be_deleted()
    {
        var (_, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var bobsRule = await CreateWithCategoryAsync(bobUser, bob, "bobs");

        var response = await SendAsync(HttpMethod.Delete, $"/rules/{bobsRule}", alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await Db.Rules.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_category_removes_the_rules_that_assign_it_from_the_list()
    {
        var (user, token) = await NewUserAsync();
        var doomed = await AddCategoryAsync(user, "Doomed");
        await CreateAsync(token, "gone", categoryId: doomed.Id);
        await CreateAsync(token, "payment", markTransfer: true);

        await SendAsync(HttpMethod.Delete, $"/categories/{doomed.Id}", token);

        Assert.Equal(["payment"], await ListedPatternsAsync(token));
    }

    // Reordering

    [Fact]
    public async Task Reordering_changes_the_list_and_rewrites_positions_from_zero()
    {
        var (user, token) = await NewUserAsync();
        var a = await CreateWithCategoryAsync(user, token, "a");
        var b = await CreateWithCategoryAsync(user, token, "b");
        var c = await CreateWithCategoryAsync(user, token, "c");
        await SendAsync(HttpMethod.Delete, $"/rules/{b}", token);
        var d = await CreateWithCategoryAsync(user, token, "d");   // positions are now 0, 2, 3

        var response = await SendAsync(HttpMethod.Post, "/rules/order", token, new { ids = new[] { d, a, c } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["d", "a", "c"], Patterns(await ReadJsonAsync(response)));
        Assert.Equal(["d", "a", "c"], await ListedPatternsAsync(token));
        Assert.Equal([0, 1, 2], await Db.Rules.AsNoTracking().OrderBy(r => r.Position).Select(r => r.Position).ToListAsync());
    }

    [Fact]
    public async Task Reordering_changes_which_rule_wins()
    {
        var (user, token) = await NewUserAsync();
        var specific = await CreateWithCategoryAsync(user, token, "amazon prime", "StartsWith");
        var broad = await CreateWithCategoryAsync(user, token, "amazon");
        var before = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules/match?description=AMAZON PRIME VIDEO", token));

        await SendAsync(HttpMethod.Post, "/rules/order", token, new { ids = new[] { broad, specific } });
        var after = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules/match?description=AMAZON PRIME VIDEO", token));

        Assert.Equal(specific, before.GetProperty("rule").GetProperty("id").GetGuid());
        Assert.Equal(broad, after.GetProperty("rule").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_order_that_is_not_exactly_the_callers_rules_is_a_422_and_changes_nothing()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var a = await CreateWithCategoryAsync(aliceUser, alice, "a");
        var b = await CreateWithCategoryAsync(aliceUser, alice, "b");
        var bobs = await CreateWithCategoryAsync(bobUser, bob, "bobs");

        var cases = new Dictionary<string, object>
        {
            ["missing one"] = new { ids = new[] { a } },
            ["repeated"] = new { ids = new[] { a, a } },
            ["unknown id"] = new { ids = new[] { a, Guid.NewGuid() } },
            ["someone else's rule"] = new { ids = new[] { a, bobs } },
            ["extra"] = new { ids = new[] { a, b, bobs } },
            ["empty"] = new { ids = Array.Empty<Guid>() },
            ["no ids field"] = new { },
        };

        foreach (var (label, body) in cases)
        {
            var response = await SendAsync(HttpMethod.Post, "/rules/order", alice, body);

            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{label}: {(int)response.StatusCode}");
            Assert.Equal("order.mismatch", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.Equal(["a", "b"], await ListedPatternsAsync(alice));
    }

    [Fact]
    public async Task Reordering_with_no_rules_and_no_ids_is_fine()
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Post, "/rules/order", token, new { ids = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Match

    [Fact]
    public async Task Match_reports_the_winning_rule_or_that_there_is_none()
    {
        var (user, token) = await NewUserAsync();
        var id = await CreateWithCategoryAsync(user, token, "starbucks");

        var hit = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules/match?description=  STARBUCKS   #12 ", token));
        var miss = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules/match?description=Rent", token));

        Assert.True(hit.GetProperty("matched").GetBoolean());
        Assert.Equal(id, hit.GetProperty("rule").GetProperty("id").GetGuid());
        Assert.False(miss.GetProperty("matched").GetBoolean());
        Assert.Equal(JsonValueKind.Null, miss.GetProperty("rule").ValueKind);
    }

    [Fact]
    public async Task Match_only_consults_the_callers_own_rules()
    {
        var (_, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        await CreateWithCategoryAsync(bobUser, bob, "starbucks");

        var result = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/rules/match?description=STARBUCKS", alice));

        Assert.False(result.GetProperty("matched").GetBoolean());
    }

    [Theory]
    [InlineData("", "description.required")]
    [InlineData("   ", "description.required")]
    public async Task Match_needs_a_description(string description, string code)
    {
        var (_, token) = await NewUserAsync();

        var response = await SendAsync(HttpMethod.Get, $"/rules/match?description={description}", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "description"));
    }

    [Fact]
    public async Task Match_without_the_parameter_at_all_is_a_400()
    {
        var (_, token) = await NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Get, "/rules/match", token)).StatusCode);
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/rules")]
    [InlineData("POST", "/rules")]
    [InlineData("GET", "/rules/match?description=x")]
    [InlineData("POST", "/rules/order")]
    [InlineData("GET", "/rules/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/rules/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/rules/00000000-0000-0000-0000-000000000001")]
    public async Task Every_endpoint_requires_a_session(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, token: null, body: new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class RuleLimitTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["Rules:MaxPerUser"] = "3" };

    private static object Rule(int i) => new { pattern = $"p{i}", matchType = "Contains", markTransfer = true };

    [Fact]
    public async Task The_rule_limit_comes_from_configuration_and_is_a_409_beyond_it()
    {
        var (_, token) = await CreateUserWithSessionAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/rules", token, Rule(i))).StatusCode);
        }

        var fourth = await SendAsync(HttpMethod.Post, "/rules", token, Rule(3));

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        Assert.Equal("rule.limit_reached", (await ReadJsonAsync(fourth)).GetProperty("code").GetString());
        Assert.Equal(3, await Db.Rules.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_rule_makes_room_again()
    {
        var (_, token) = await CreateUserWithSessionAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await ReadJsonAsync(await SendAsync(HttpMethod.Post, "/rules", token, Rule(i)))).GetProperty("id").GetGuid());
        }

        await SendAsync(HttpMethod.Delete, $"/rules/{ids[0]}", token);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, "/rules", token, Rule(9))).StatusCode);
    }

    [Fact]
    public async Task The_limit_is_per_user()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (_, bob) = await CreateUserWithSessionAsync();
        for (var i = 0; i < 3; i++)
        {
            await SendAsync(HttpMethod.Post, "/rules", alice, Rule(i));
        }

        var bobsFirst = await SendAsync(HttpMethod.Post, "/rules", bob, Rule(0));

        Assert.Equal(HttpStatusCode.Created, bobsFirst.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_creates_never_exceed_the_limit_and_never_share_a_position()
    {
        // Without the per-user lock every request would count the rules it can see, find room, and
        // insert, ending above the limit with duplicate positions.
        var (user, token) = await CreateUserWithSessionAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            SendAsync(HttpMethod.Post, "/rules", token, Rule(i))));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(
            [0, 1, 2],
            await Db.Rules.AsNoTracking().Where(r => r.UserId == user.Id).OrderBy(r => r.Position).Select(r => r.Position).ToListAsync());
    }

    [Fact]
    public async Task A_nonsensical_limit_stops_the_application_starting()
    {
        using var factory = new ApiFactory(
            ConnectionString, new Dictionary<string, string?> { ["Rules:MaxPerUser"] = "0" }, Clock);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("MaxPerUser", exception.ToString());
    }
}
