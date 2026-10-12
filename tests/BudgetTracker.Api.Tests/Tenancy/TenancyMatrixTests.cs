using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Tenancy;

/// <summary>
/// Two users each own one of everything. One then attacks the other through every endpoint and
/// every kind of cross-reference. Per-resource tests check these one at a time; this checks the
/// whole surface together, including interactions no single resource owns (for example whether a
/// request that mentions another user's archived account reveals that it exists).
/// </summary>
public class TenancyMatrixTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private sealed record Person(
        string Token, Account Account, Account Archived, Category Category, Transaction Transaction);

    private sealed record Attempt(string Label, HttpMethod Method, string Url, object? Body, HttpStatusCode Expected, string Code);

    private async Task<Person> SeedAsync(string label)
    {
        var (user, token) = await CreateUserWithSessionAsync($"{label}@example.com");
        var account = new Account { UserId = user.Id, Name = $"{label}-account" };
        var archived = new Account { UserId = user.Id, Name = $"{label}-closed", ArchivedAt = Clock.GetUtcNow() };
        var category = new Category { UserId = user.Id, Name = $"{label}-category" };
        Db.AddRange(account, archived, category);
        await Db.SaveChangesAsync();

        var transaction = new Transaction
        {
            UserId = user.Id,
            AccountId = account.Id,
            CategoryId = category.Id,
            PostedOn = new DateOnly(2026, 10, 1),
            Amount = -12.5m,
            Description = $"{label}-transaction",
        };
        Db.Add(transaction);
        Db.Add(new Budget { UserId = user.Id, CategoryId = category.Id, Month = new DateOnly(2026, 10, 1), Amount = 300m });
        await Db.SaveChangesAsync();
        return new Person(token, account, archived, category, transaction);
    }

    /// <summary>Every row of every table, in a fixed order, as one string.</summary>
    private async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
    {
        users = await Db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => new { u.Id, u.Email, u.PasswordHash }).ToListAsync(),
        accounts = await Db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Id, a.UserId, a.Name, a.Type, a.InvertSign, a.ArchivedAt }).ToListAsync(),
        categories = await Db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Id, c.UserId, c.Name }).ToListAsync(),
        transactions = await Db.Transactions.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.UserId, t.AccountId, t.CategoryId, t.PostedOn, t.Amount, t.Description, t.IsTransfer, t.CreatedAt }).ToListAsync(),
        budgets = await Db.Budgets.AsNoTracking().OrderBy(b => b.Id).Select(b => new { b.Id, b.UserId, b.CategoryId, b.Month, b.Amount }).ToListAsync(),
        sessions = await Db.Sessions.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.UserId }).ToListAsync(),
    });

    private static IEnumerable<Attempt> Attempts(Person owner, Person attacker)
    {
        var notFound = HttpStatusCode.NotFound;
        var refused = HttpStatusCode.UnprocessableEntity;
        var ownAccount = attacker.Account.Id;
        object TransactionBody(Guid account, Guid? category = null) =>
            new { accountId = account, categoryId = category, postedOn = "2026-10-02", amount = -1m, description = "attack" };

        // Someone else's resource looks like it does not exist.
        yield return new("read their account", HttpMethod.Get, $"/accounts/{owner.Account.Id}", null, notFound, "not_found");
        yield return new("read their archived account", HttpMethod.Get, $"/accounts/{owner.Archived.Id}", null, notFound, "not_found");
        yield return new("replace their account", HttpMethod.Put, $"/accounts/{owner.Account.Id}", new { name = "x", type = "Credit" }, notFound, "not_found");
        yield return new("archive their account", HttpMethod.Delete, $"/accounts/{owner.Account.Id}", null, notFound, "not_found");
        yield return new("unarchive their account", HttpMethod.Post, $"/accounts/{owner.Archived.Id}/unarchive", null, notFound, "not_found");
        yield return new("permanently delete their archived account", HttpMethod.Delete, $"/accounts/{owner.Archived.Id}?permanent=true", null, notFound, "not_found");
        yield return new("read their category", HttpMethod.Get, $"/categories/{owner.Category.Id}", null, notFound, "not_found");
        yield return new("rename their category", HttpMethod.Put, $"/categories/{owner.Category.Id}", new { name = "x" }, notFound, "not_found");
        yield return new("delete their category", HttpMethod.Delete, $"/categories/{owner.Category.Id}", null, notFound, "not_found");
        yield return new("read their transaction", HttpMethod.Get, $"/transactions/{owner.Transaction.Id}", null, notFound, "not_found");
        yield return new("replace their transaction", HttpMethod.Put, $"/transactions/{owner.Transaction.Id}", TransactionBody(ownAccount), notFound, "not_found");
        yield return new("delete their transaction", HttpMethod.Delete, $"/transactions/{owner.Transaction.Id}", null, notFound, "not_found");

        // Referring to someone else's resource is refused by the database, and the answer is
        // the same whether or not the account is archived: 422, never 409, which would confirm
        // that it exists and is archived.
        yield return new("create a transaction in their account", HttpMethod.Post, "/transactions", TransactionBody(owner.Account.Id), refused, "account.invalid");
        yield return new("create a transaction in their archived account", HttpMethod.Post, "/transactions", TransactionBody(owner.Archived.Id), refused, "account.invalid");
        yield return new("create a transaction with their category", HttpMethod.Post, "/transactions", TransactionBody(ownAccount, owner.Category.Id), refused, "category.invalid");
        yield return new("move my transaction into their account", HttpMethod.Put, $"/transactions/{attacker.Transaction.Id}", TransactionBody(owner.Account.Id), refused, "account.invalid");
        yield return new("move my transaction into their archived account", HttpMethod.Put, $"/transactions/{attacker.Transaction.Id}", TransactionBody(owner.Archived.Id), refused, "account.invalid");
        yield return new("categorize my transaction with their category", HttpMethod.Put, $"/transactions/{attacker.Transaction.Id}", TransactionBody(ownAccount, owner.Category.Id), refused, "category.invalid");
    }

    private async Task AssertCannotTouchAsync(Person owner, Person attacker)
    {
        var before = await SnapshotAsync();
        var failures = new List<string>();

        foreach (var attempt in Attempts(owner, attacker))
        {
            var response = await SendAsync(attempt.Method, attempt.Url, attacker.Token, attempt.Body);
            var code = response.Content.Headers.ContentLength is 0
                ? ""
                : (await ReadJsonAsync(response)).TryGetProperty("code", out var c) ? c.GetString() : "";

            if (response.StatusCode != attempt.Expected || code != attempt.Code)
            {
                failures.Add($"{attempt.Label}: expected {(int)attempt.Expected} {attempt.Code}, got {(int)response.StatusCode} {code}");
            }
        }

        // Lists show only the caller's own data, even with filters aimed at the other user's ids.
        var ownerFragments = new[]
        {
            owner.Account.Id.ToString(), owner.Archived.Id.ToString(), owner.Category.Id.ToString(),
            owner.Transaction.Id.ToString(), owner.Account.Name, owner.Archived.Name,
            owner.Category.Name, owner.Transaction.Description,
        };

        foreach (var url in new[]
        {
            "/accounts?includeArchived=true", "/categories", "/transactions",
            $"/transactions?accountId={owner.Account.Id}", $"/transactions?categoryId={owner.Category.Id}",
        })
        {
            var text = await (await SendAsync(HttpMethod.Get, url, attacker.Token)).Content.ReadAsStringAsync();
            foreach (var fragment in ownerFragments.Where(text.Contains))
            {
                failures.Add($"GET {url} leaked {fragment}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Alice_cannot_read_change_or_reference_anything_of_Bobs()
    {
        var alice = await SeedAsync("alice");
        var bob = await SeedAsync("bob");

        await AssertCannotTouchAsync(owner: bob, attacker: alice);
    }

    [Fact]
    public async Task And_the_same_holds_the_other_way_round()
    {
        var alice = await SeedAsync("alice");
        var bob = await SeedAsync("bob");

        await AssertCannotTouchAsync(owner: alice, attacker: bob);
    }

    [Fact]
    public async Task Each_user_still_sees_exactly_their_own_data_afterwards()
    {
        var alice = await SeedAsync("alice");
        var bob = await SeedAsync("bob");
        await AssertCannotTouchAsync(owner: bob, attacker: alice);

        var accounts = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/accounts?includeArchived=true", alice.Token));
        var transactions = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/transactions", alice.Token));

        Assert.Equal(
            new[] { alice.Account.Id, alice.Archived.Id }.Order().ToArray(),
            accounts.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Order().ToArray());
        Assert.Equal(
            [alice.Transaction.Id],
            transactions.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToArray());
    }
}
