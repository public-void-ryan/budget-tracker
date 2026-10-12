using System.Net;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Auth;

public class SessionLifetimeTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task A_session_works_until_the_instant_it_expires_and_not_after()
    {
        // The lifetime is 90 minutes on purpose. The application's purger ticks hourly on this
        // same fake clock and deletes expired rows, so a lifetime that is a whole number of
        // hours would put a purge tick exactly on the boundary: the 401 would then come from
        // the row being gone, and this test would pass even if the handler's own check broke.
        var (_, token) = await CreateUserWithSessionAsync(lifetime: TimeSpan.FromMinutes(90));

        Clock.Advance(TimeSpan.FromMinutes(90) - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Db.Sessions.CountAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", token))).StatusCode);
    }

    [Fact]
    public async Task Logging_in_again_after_expiry_gives_a_fresh_working_session()
    {
        var (user, oldToken) = await CreateUserWithSessionAsync(password: GoodPassword);
        Clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", oldToken))).StatusCode);

        var login = await PostAsync("/auth/login", new { email = user.Email, password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var newToken = (await ReadJsonAsync(login)).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", newToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", oldToken))).StatusCode);
    }
}

public class ConfiguredLifetimeTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["Sessions:LifetimeDays"] = "2" };

    [Fact]
    public async Task The_session_lifetime_comes_from_configuration()
    {
        var (user, _) = await CreateUserWithSessionAsync(password: GoodPassword);

        var login = await PostAsync("/auth/login", new { email = user.Email, password = GoodPassword });

        var expiresAt = (await ReadJsonAsync(login)).GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(Clock.GetUtcNow().AddDays(2), expiresAt);
    }
}
