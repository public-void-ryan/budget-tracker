using System.Net;
using BudgetTracker.Api.Tests.Database;

namespace BudgetTracker.Api.Tests.Auth;

public class RateLimitTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["RateLimiting:Auth:PermitLimit"] = "3" };

    private Task<HttpResponseMessage> FailedLoginAsync() =>
        PostAsync("/auth/login", new { email = "nobody@example.com", password = "not the password" });

    [Fact]
    public async Task Attempts_beyond_the_limit_get_a_429_with_retry_after()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await FailedLoginAsync()).StatusCode);
        }

        var limited = await FailedLoginAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Equal("rate_limited", (await ReadJsonAsync(limited)).GetProperty("code").GetString());
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task Register_and_login_share_one_budget()
    {
        for (var i = 0; i < 3; i++)
        {
            await FailedLoginAsync();
        }

        var register = await PostAsync("/auth/register", new { email = "ryan@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, register.StatusCode);
    }

    [Fact]
    public async Task Authenticated_endpoints_are_not_rate_limited()
    {
        var (_, token) = await CreateUserWithSessionAsync();
        for (var i = 0; i < 4; i++)
        {
            await FailedLoginAsync();
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);
        }
    }
}
