using System.Net;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Auth;

public class BearerAuthenticationTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task Me_returns_the_authenticated_user_and_nothing_secret()
    {
        var (user, token) = await CreateUserWithSessionAsync("ryan@example.com");

        var response = await Client.SendAsync(Get("/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(user.Id, body.GetProperty("id").GetGuid());
        Assert.Equal("ryan@example.com", body.GetProperty("email").GetString());
        Assert.Equal(["email", "id"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task A_request_without_credentials_gets_a_401_problem_with_a_bearer_challenge()
    {
        var response = await Client.SendAsync(Get("/me"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(h => h.Scheme));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("auth.required", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("")]
    public async Task A_malformed_token_is_a_401(string token)
    {
        var response = await Client.SendAsync(Get("/me", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_of_the_right_length_with_illegal_characters_is_a_401_not_a_500()
    {
        var response = await Client.SendAsync(Get("/me", new string('!', 43)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_well_formed_token_that_was_never_issued_is_a_401()
    {
        var response = await Client.SendAsync(Get("/me", SessionTokens.Generate().Token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_sent_under_the_wrong_scheme_is_a_401()
    {
        var (_, token) = await CreateUserWithSessionAsync();

        var response = await Client.SendAsync(Get("/me", token, scheme: "Basic"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_session_is_rejected_even_though_its_row_still_exists()
    {
        var (_, token) = await CreateUserWithSessionAsync(lifetime: TimeSpan.FromDays(1), age: TimeSpan.FromDays(2));

        var response = await Client.SendAsync(Get("/me", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task An_older_session_that_has_not_expired_still_works()
    {
        var (_, token) = await CreateUserWithSessionAsync(lifetime: TimeSpan.FromDays(14), age: TimeSpan.FromDays(10));

        var response = await Client.SendAsync(Get("/me", token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Each_token_resolves_to_its_own_user()
    {
        var (alice, aliceToken) = await CreateUserWithSessionAsync("alice@example.com");
        var (bob, bobToken) = await CreateUserWithSessionAsync("bob@example.com");

        var asAlice = await ReadJsonAsync(await Client.SendAsync(Get("/me", aliceToken)));
        var asBob = await ReadJsonAsync(await Client.SendAsync(Get("/me", bobToken)));

        Assert.Equal(alice.Id, asAlice.GetProperty("id").GetGuid());
        Assert.Equal(bob.Id, asBob.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Deleting_the_session_row_revokes_the_token_immediately()
    {
        var (_, token) = await CreateUserWithSessionAsync();
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);

        await Db.Sessions.ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", token))).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_user_revokes_their_tokens()
    {
        var (user, token) = await CreateUserWithSessionAsync();

        await Db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", token))).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_only_the_session_it_was_called_with()
    {
        var first = await RegisterAndLoginAsync("ryan@example.com");
        var login = await PostAsync("/auth/login", new { email = "ryan@example.com", password = GoodPassword });
        var second = (await ReadJsonAsync(login)).GetProperty("token").GetString()!;

        var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logout.Headers.Authorization = new("Bearer", first);
        var response = await Client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", second))).StatusCode);
        Assert.Equal(1, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task Logout_without_a_token_is_a_401()
    {
        var response = await Client.PostAsync("/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_health_endpoint_needs_no_authentication()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
