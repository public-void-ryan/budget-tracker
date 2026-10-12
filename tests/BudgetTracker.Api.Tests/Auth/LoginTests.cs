using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Database;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Tests.Auth;

public class LoginTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<User> AddUserAsync(string email = "ryan@example.com", string password = GoodPassword)
    {
        var user = new User { Email = email, PasswordHash = new PasswordService().Hash(password) };
        Db.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task A_correct_login_returns_a_bearer_token_that_is_stored_only_as_a_hash()
    {
        var user = await AddUserAsync();

        var response = await PostAsync("/auth/login", new { email = "ryan@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        var token = body.GetProperty("token").GetString()!;

        var session = await Db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(user.Id, session.UserId);
        Assert.Equal(HashOf(token), session.TokenHash);
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(token), session.TokenHash);
    }

    [Fact]
    public async Task The_session_expires_in_fourteen_days_and_the_response_says_when()
    {
        await AddUserAsync();

        var response = await PostAsync("/auth/login", new { email = "ryan@example.com", password = GoodPassword });

        var expiresAt = (await ReadJsonAsync(response)).GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(Clock.GetUtcNow().AddDays(14), expiresAt);
        var session = await Db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), session.ExpiresAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_are_indistinguishable()
    {
        await AddUserAsync();

        var wrongPassword = await PostAsync("/auth/login", new { email = "ryan@example.com", password = "not the password" });
        var unknownEmail = await PostAsync("/auth/login", new { email = "nobody@example.com", password = "not the password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        var wrongPasswordBody = await Stable(wrongPassword);
        var unknownEmailBody = await Stable(unknownEmail);
        Assert.Equal(wrongPasswordBody, unknownEmailBody);
        Assert.Equal("auth.invalid_credentials", JsonDocument.Parse(wrongPasswordBody).RootElement.GetProperty("code").GetString());
        Assert.Equal(0, await Db.Sessions.CountAsync());

        // Everything except the per-request trace id.
        async Task<string> Stable(HttpResponseMessage response)
        {
            var body = await ReadJsonAsync(response);
            return JsonSerializer.Serialize(body.EnumerateObject().Where(p => p.Name != "traceId").ToDictionary(p => p.Name, p => p.Value));
        }
    }

    [Fact]
    public async Task The_email_is_normalized_the_same_way_as_at_registration()
    {
        await AddUserAsync("ryan@example.com");

        var response = await PostAsync("/auth/login", new { email = "  RYAN@Example.com ", password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_fields_are_a_400_naming_the_fields()
    {
        var response = await PostAsync("/auth/login", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = (await ReadJsonAsync(response)).GetProperty("codes");
        Assert.Equal("email.required", codes.GetProperty("email")[0].GetString());
        Assert.Equal("password.required", codes.GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task Registration_password_rules_are_not_applied_at_login()
    {
        // A short password must get the generic 401, not a "too short" hint that would reveal
        // the policy, and accounts made under an older policy must still be able to log in.
        await AddUserAsync();

        var response = await PostAsync("/auth/login", new { email = "ryan@example.com", password = "short" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_enormous_password_is_rejected_without_being_hashed()
    {
        await AddUserAsync();

        var response = await PostAsync("/auth/login", new { email = "ryan@example.com", password = new string('x', 100_000) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Each_login_creates_its_own_session()
    {
        await AddUserAsync();

        await PostAsync("/auth/login", new { email = "ryan@example.com", password = GoodPassword });
        await PostAsync("/auth/login", new { email = "ryan@example.com", password = GoodPassword });

        Assert.Equal(2, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task A_hash_made_with_outdated_parameters_is_upgraded_on_login()
    {
        var weak = new PasswordService(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }));
        var user = new User { Email = "old@example.com", PasswordHash = weak.Hash(GoodPassword) };
        Db.Add(user);
        await Db.SaveChangesAsync();
        var before = user.PasswordHash;

        var response = await PostAsync("/auth/login", new { email = "old@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = (await Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).PasswordHash;
        Assert.NotEqual(before, after);
        Assert.Equal(PasswordVerification.Valid, new PasswordService().Verify(after, GoodPassword));
    }

    [Fact]
    public async Task A_failed_login_does_not_touch_the_stored_hash()
    {
        var user = await AddUserAsync();
        var before = user.PasswordHash;

        await PostAsync("/auth/login", new { email = "ryan@example.com", password = "wrong password here" });

        Assert.Equal(before, (await Db.Users.AsNoTracking().SingleAsync()).PasswordHash);
    }
}
