using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Transactions;

public class TransactionTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<Account> AddAccountAsync(User user, string name = "Checking", bool archived = false)
    {
        var account = new Account
        {
            UserId = user.Id,
            Name = name,
            ArchivedAt = archived ? Clock.GetUtcNow() : null,
        };
        Db.Add(account);
        await Db.SaveChangesAsync();
        return account;
    }

    private async Task<Category> AddCategoryAsync(User user, string name = "Food")
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    private async Task<Transaction> AddTransactionAsync(User user, Account account, Category? category = null)
    {
        var transaction = new Transaction
        {
            UserId = user.Id,
            AccountId = account.Id,
            CategoryId = category?.Id,
            PostedOn = new DateOnly(2026, 10, 1),
            Amount = -12.5m,
            Description = "coffee",
        };
        Db.Add(transaction);
        await Db.SaveChangesAsync();
        return transaction;
    }

    private static object Body(
        Guid? accountId, Guid? categoryId = null, string? postedOn = "2026-10-01", decimal? amount = -12.5m,
        string? description = "coffee", bool? isTransfer = null) =>
        new { accountId, categoryId, postedOn, amount, description, isTransfer };

    private static string Code(JsonElement problem, string field) =>
        problem.GetProperty("codes").GetProperty(field)[0].GetString()!;

    // Create

    [Fact]
    public async Task Creating_a_transaction_returns_201_with_a_location_and_the_full_resource()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        var category = await AddCategoryAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token,
            Body(account.Id, category.Id, "2026-10-01", -12.5m, "coffee", isTransfer: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/transactions/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal(
            ["accountId", "amount", "categoryId", "createdAt", "description", "id", "isTransfer", "postedOn"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("2026-10-01", body.GetProperty("postedOn").GetString());
        Assert.Equal(-12.5m, body.GetProperty("amount").GetDecimal());

        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal((id, user.Id, account.Id, category.Id), (stored.Id, stored.UserId, stored.AccountId, stored.CategoryId));
        Assert.Null(stored.ImportHash);
        Assert.Null(stored.ImportSeq);
    }

    [Fact]
    public async Task The_category_is_optional_and_the_description_is_trimmed()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, description: "  coffee  "));

        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Null(stored.CategoryId);
        Assert.Equal("coffee", stored.Description);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task The_transfer_flag_defaults_to_false(bool? sent, bool expected)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, isTransfer: sent));

        Assert.Equal(expected, (await Db.Transactions.AsNoTracking().SingleAsync()).IsTransfer);
    }

    [Theory]
    [InlineData("-12.5")]
    [InlineData("12.5")]
    [InlineData("0.0001")]
    [InlineData("-1234567.8912")]
    [InlineData("12.50000")] // more digits written, same value: not a precision problem
    [InlineData("999999999999999.9999")]
    [InlineData("-999999999999999.9999")]
    public async Task Valid_amounts_are_stored_exactly(string amount)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, amount: decimal.Parse(amount)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(decimal.Parse(amount), (await Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData("0", "amount.zero")]
    [InlineData("0.0000", "amount.zero")]
    [InlineData("0.00001", "amount.too_precise")]
    [InlineData("12.34567", "amount.too_precise")]
    [InlineData("-12.34567", "amount.too_precise")]
    [InlineData("1000000000000000", "amount.out_of_range")]
    [InlineData("-1000000000000000", "amount.out_of_range")]
    public async Task An_unusable_amount_is_a_400_and_nothing_is_stored_or_rounded(string amount, string code)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, amount: decimal.Parse(amount)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "amount"));
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_missing_amount_is_required()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, amount: null));

        Assert.Equal("amount.required", Code(await ReadJsonAsync(response), "amount"));
    }

    [Fact]
    public async Task An_amount_sent_as_a_string_is_rejected_not_parsed()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token,
            new { accountId = account.Id, postedOn = "2026-10-01", amount = "12.50", description = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Theory]
    [InlineData(null, "description.required")]
    [InlineData("   ", "description.required")]
    [InlineData("two\nlines", "description.invalid")]
    public async Task An_unusable_description_is_a_400(string? description, string code)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, description: description));

        Assert.Equal(code, Code(await ReadJsonAsync(response), "description"));
    }

    [Fact]
    public async Task A_description_may_be_five_hundred_characters_and_not_one_more()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var ok = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, description: new string('d', 500)));
        var tooLong = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, description: new string('d', 501)));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal("description.too_long", Code(await ReadJsonAsync(tooLong), "description"));
    }

    [Theory]
    [InlineData(null, "postedOn.required")]
    [InlineData("", "postedOn.required")]
    [InlineData("10/01/2026", "postedOn.invalid")]
    [InlineData("2026-13-01", "postedOn.invalid")]
    [InlineData("2026-02-30", "postedOn.invalid")]
    [InlineData("2026-10-1", "postedOn.invalid")]
    [InlineData("20261001", "postedOn.invalid")]
    [InlineData("2026-10-01T10:00:00Z", "postedOn.invalid")]
    [InlineData(" 2026-10-01", "postedOn.invalid")]
    [InlineData("not a date", "postedOn.invalid")]
    public async Task Only_a_real_iso_date_is_accepted(string? postedOn, string code)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id, postedOn: postedOn));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, Code(await ReadJsonAsync(response), "postedOn"));
    }

    [Fact]
    public async Task A_missing_account_is_required_and_every_problem_is_reported_at_once()
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var response = await SendAsync(HttpMethod.Post, "/transactions", token,
            Body(accountId: null, postedOn: "nope", amount: 0m, description: ""));

        var problem = await ReadJsonAsync(response);
        Assert.Equal("accountId.required", Code(problem, "accountId"));
        Assert.Equal("postedOn.invalid", Code(problem, "postedOn"));
        Assert.Equal("amount.zero", Code(problem, "amount"));
        Assert.Equal("description.required", Code(problem, "description"));
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_a_422()
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var response = await SendAsync(HttpMethod.Post, "/transactions", token, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account.invalid", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Another_users_account_is_refused_by_the_database_as_a_422()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsAccount = await AddAccountAsync(bob);

        var response = await SendAsync(HttpMethod.Post, "/transactions", alice, Body(bobsAccount.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account.invalid", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_category_that_is_missing_or_belongs_to_someone_else_is_a_422()
    {
        var (aliceUser, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(aliceUser);
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");

        var someoneElses = await SendAsync(HttpMethod.Post, "/transactions", alice, Body(account.Id, bobsCategory.Id));
        var missing = await SendAsync(HttpMethod.Post, "/transactions", alice, Body(account.Id, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal("category.invalid", (await ReadJsonAsync(someoneElses)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task An_archived_account_refuses_new_transactions_until_it_is_unarchived()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user, archived: true);

        var refused = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id));
        await SendAsync(HttpMethod.Post, $"/accounts/{account.Id}/unarchive", token);
        var accepted = await SendAsync(HttpMethod.Post, "/transactions", token, Body(account.Id));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("account.archived", (await ReadJsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(1, await Db.Transactions.CountAsync());
    }

    // Read

    [Fact]
    public async Task One_transaction_can_be_fetched_by_id()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var transaction = await AddTransactionAsync(user, await AddAccountAsync(user));

        var response = await SendAsync(HttpMethod.Get, $"/transactions/{transaction.Id}", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("coffee", (await ReadJsonAsync(response)).GetProperty("description").GetString());
    }

    [Fact]
    public async Task Another_users_transaction_looks_exactly_like_one_that_does_not_exist()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsTransaction = await AddTransactionAsync(bob, await AddAccountAsync(bob));

        var someoneElses = await SendAsync(HttpMethod.Get, $"/transactions/{bobsTransaction.Id}", alice);
        var missing = await SendAsync(HttpMethod.Get, $"/transactions/{Guid.NewGuid()}", alice);
        var malformed = await SendAsync(HttpMethod.Get, "/transactions/not-a-guid", alice);

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(someoneElses)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Transactions_in_an_archived_account_are_still_readable()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user, archived: true);
        var transaction = await AddTransactionAsync(user, account);

        var one = await SendAsync(HttpMethod.Get, $"/transactions/{transaction.Id}", token);
        var list = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/transactions", token));

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Single(list.GetProperty("items").EnumerateArray());
    }

    // Update

    [Fact]
    public async Task Putting_replaces_every_field_and_keeps_the_creation_time()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        var category = await AddCategoryAsync(user);
        var transaction = await AddTransactionAsync(user, account);
        var createdAt = (await Db.Transactions.AsNoTracking().SingleAsync()).CreatedAt;

        var response = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token,
            Body(account.Id, category.Id, "2026-09-15", 99.99m, "  refund  ", isTransfer: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal(
            (category.Id, new DateOnly(2026, 9, 15), 99.99m, "refund", true, createdAt),
            (stored.CategoryId, stored.PostedOn, stored.Amount, stored.Description, stored.IsTransfer, stored.CreatedAt));
    }

    [Fact]
    public async Task A_transaction_can_move_to_another_of_your_accounts()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var first = await AddAccountAsync(user, "First");
        var second = await AddAccountAsync(user, "Second");
        var transaction = await AddTransactionAsync(user, first);

        var response = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token, Body(second.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(second.Id, (await Db.Transactions.AsNoTracking().SingleAsync()).AccountId);
    }

    [Fact]
    public async Task A_null_category_clears_it_and_leaving_the_transfer_flag_out_resets_it()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(user);
        var category = await AddCategoryAsync(user);
        var transaction = await AddTransactionAsync(user, account, category);
        transaction.IsTransfer = true;
        await Db.SaveChangesAsync();

        await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token, Body(account.Id, categoryId: null));

        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Null(stored.CategoryId);
        Assert.False(stored.IsTransfer);
    }

    [Fact]
    public async Task A_refused_put_changes_nothing()
    {
        var (aliceUser, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var account = await AddAccountAsync(aliceUser);
        var bobsAccount = await AddAccountAsync(bob);
        var bobsCategory = await AddCategoryAsync(bob, "Bobs");
        var transaction = await AddTransactionAsync(aliceUser, account);

        var invalid = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", alice, Body(account.Id, amount: 0m));
        var foreignAccount = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", alice, Body(bobsAccount.Id, amount: 5m));
        var foreignCategory = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", alice, Body(account.Id, bobsCategory.Id, amount: 5m));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignAccount.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreignCategory.StatusCode);
        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal((account.Id, (Guid?)null, -12.5m), (stored.AccountId, stored.CategoryId, stored.Amount));
    }

    [Fact]
    public async Task Another_users_transaction_cannot_be_replaced_and_a_missing_one_is_a_404_before_validation()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsAccount = await AddAccountAsync(bob);
        var bobsTransaction = await AddTransactionAsync(bob, bobsAccount);

        var someoneElses = await SendAsync(HttpMethod.Put, $"/transactions/{bobsTransaction.Id}", alice, Body(bobsAccount.Id, amount: 1m));
        var missingWithBadBody = await SendAsync(HttpMethod.Put, $"/transactions/{Guid.NewGuid()}", alice, new { });

        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingWithBadBody.StatusCode);
        Assert.Equal(-12.5m, (await Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task A_transaction_in_an_archived_account_cannot_be_edited_or_moved_out()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var archived = await AddAccountAsync(user, "Closed", archived: true);
        var open = await AddAccountAsync(user, "Open");
        var transaction = await AddTransactionAsync(user, archived);

        var edit = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token, Body(archived.Id, amount: 1m));
        var moveOut = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token, Body(open.Id));

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, moveOut.StatusCode);
        var stored = await Db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal((archived.Id, -12.5m), (stored.AccountId, stored.Amount));
    }

    [Fact]
    public async Task A_transaction_cannot_be_moved_into_an_archived_account()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var open = await AddAccountAsync(user, "Open");
        var archived = await AddAccountAsync(user, "Closed", archived: true);
        var transaction = await AddTransactionAsync(user, open);

        var response = await SendAsync(HttpMethod.Put, $"/transactions/{transaction.Id}", token, Body(archived.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(open.Id, (await Db.Transactions.AsNoTracking().SingleAsync()).AccountId);
    }

    // Delete

    [Fact]
    public async Task Deleting_removes_the_transaction_and_a_second_delete_is_a_404()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var transaction = await AddTransactionAsync(user, await AddAccountAsync(user));

        var first = await SendAsync(HttpMethod.Delete, $"/transactions/{transaction.Id}", token);
        var second = await SendAsync(HttpMethod.Delete, $"/transactions/{transaction.Id}", token);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Another_users_transaction_cannot_be_deleted()
    {
        var (_, alice) = await CreateUserWithSessionAsync();
        var (bob, _) = await CreateUserWithSessionAsync();
        var bobsTransaction = await AddTransactionAsync(bob, await AddAccountAsync(bob));

        var response = await SendAsync(HttpMethod.Delete, $"/transactions/{bobsTransaction.Id}", alice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_transaction_in_an_archived_account_cannot_be_deleted()
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var transaction = await AddTransactionAsync(user, await AddAccountAsync(user, archived: true));

        var response = await SendAsync(HttpMethod.Delete, $"/transactions/{transaction.Id}", token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await Db.Transactions.CountAsync());
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/transactions")]
    [InlineData("POST", "/transactions")]
    [InlineData("GET", "/transactions/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/transactions/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/transactions/00000000-0000-0000-0000-000000000001")]
    public async Task Every_endpoint_requires_a_session(string method, string url)
    {
        var response = await SendAsync(new HttpMethod(method), url, token: null, body: new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
