using System.Net;
using System.Net.Http.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Auth;

public class AccountDeletionTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private sealed record Counts(int Users, int Accounts, int Categories, int Transactions, int Budgets, int Sessions);

    private async Task SeedOwnedDataAsync(User user)
    {
        var account = new Account { UserId = user.Id, Name = "Checking" };
        var category = new Category { UserId = user.Id, Name = "Food" };
        Db.AddRange(account, category);
        await Db.SaveChangesAsync();

        Db.Add(new Transaction
        {
            UserId = user.Id,
            AccountId = account.Id,
            CategoryId = category.Id,
            PostedOn = new DateOnly(2026, 10, 1),
            Amount = -4.5m,
            Description = "coffee",
        });
        Db.Add(new Budget
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Month = new DateOnly(2026, 10, 1),
            Amount = 300m,
        });
        await Db.SaveChangesAsync();
    }

    private async Task<Counts> CountsForAsync(Guid userId) => new(
        await Db.Users.CountAsync(u => u.Id == userId),
        await Db.Accounts.CountAsync(a => a.UserId == userId),
        await Db.Categories.CountAsync(c => c.UserId == userId),
        await Db.Transactions.CountAsync(t => t.UserId == userId),
        await Db.Budgets.CountAsync(b => b.UserId == userId),
        await Db.Sessions.CountAsync(s => s.UserId == userId));

    private Task<HttpResponseMessage> DeleteMeAsync(string? token, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/me");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return Client.SendAsync(request);
    }

    private async Task<string> AddSecondSessionAsync(User user)
    {
        var (token, hash) = BudgetTracker.Api.Auth.SessionTokens.Generate();
        Db.Add(new Session
        {
            UserId = user.Id,
            TokenHash = hash,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(14),
        });
        await Db.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_user_and_every_row_they_own()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);
        await SeedOwnedDataAsync(user);
        await AddSecondSessionAsync(user);
        Assert.Equal(new Counts(1, 1, 1, 1, 1, 2), await CountsForAsync(user.Id));

        var response = await DeleteMeAsync(token, new { password = GoodPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new Counts(0, 0, 0, 0, 0, 0), await CountsForAsync(user.Id));
    }

    [Fact]
    public async Task Another_users_data_is_left_exactly_as_it_was()
    {
        var (alice, aliceToken) = await CreateUserWithSessionAsync(password: GoodPassword);
        var (bob, _) = await CreateUserWithSessionAsync();
        await SeedOwnedDataAsync(alice);
        await SeedOwnedDataAsync(bob);
        var bobBefore = await CountsForAsync(bob.Id);

        await DeleteMeAsync(aliceToken, new { password = GoodPassword });

        Assert.Equal(new Counts(1, 1, 1, 1, 1, 1), bobBefore);
        Assert.Equal(bobBefore, await CountsForAsync(bob.Id));
    }

    [Fact]
    public async Task Every_session_of_the_deleted_user_stops_working()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);
        var otherDeviceToken = await AddSecondSessionAsync(user);

        await DeleteMeAsync(token, new { password = GoodPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", otherDeviceToken))).StatusCode);
    }

    [Fact]
    public async Task The_email_can_be_registered_again_afterwards()
    {
        var (user, token) = await CreateUserWithSessionAsync("ryan@example.com", password: GoodPassword);

        await DeleteMeAsync(token, new { password = GoodPassword });
        var again = await PostAsync("/auth/register", new { email = user.Email, password = GoodPassword });

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_a_403_and_deletes_nothing()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);
        await SeedOwnedDataAsync(user);

        var response = await DeleteMeAsync(token, new { password = "not the password" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.incorrect_password", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(new Counts(1, 1, 1, 1, 1, 1), await CountsForAsync(user.Id));

        // The session is still fine: it was a confirmation that failed, not authentication.
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);
    }

    [Fact]
    public async Task An_enormous_password_is_a_403_without_being_hashed_and_deletes_nothing()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);

        var response = await DeleteMeAsync(token, new { password = new string('x', 100_000) });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, (await CountsForAsync(user.Id)).Users);
    }

    [Fact]
    public async Task A_missing_password_is_a_400_and_deletes_nothing()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);

        var response = await DeleteMeAsync(token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "password.required",
            (await ReadJsonAsync(response)).GetProperty("codes").GetProperty("password")[0].GetString());
        Assert.Equal(1, (await CountsForAsync(user.Id)).Users);
    }

    [Fact]
    public async Task A_request_with_no_body_is_a_400_and_deletes_nothing()
    {
        var (user, token) = await CreateUserWithSessionAsync(password: GoodPassword);

        var response = await DeleteMeAsync(token, body: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, (await CountsForAsync(user.Id)).Users);
    }

    [Fact]
    public async Task Without_a_session_the_request_is_a_401_and_deletes_nothing()
    {
        var (user, _) = await CreateUserWithSessionAsync(password: GoodPassword);

        var response = await DeleteMeAsync(token: null, new { password = GoodPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, (await CountsForAsync(user.Id)).Users);
    }

    [Fact]
    public async Task Knowing_the_password_is_not_enough_without_being_logged_in_as_that_user()
    {
        // Alice's session cannot be used to delete Bob, even with Bob's password.
        var (_, aliceToken) = await CreateUserWithSessionAsync(password: GoodPassword);
        var (bob, _) = await CreateUserWithSessionAsync(password: "bobs own password 1");

        var response = await DeleteMeAsync(aliceToken, new { password = "bobs own password 1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, (await CountsForAsync(bob.Id)).Users);
    }
}
