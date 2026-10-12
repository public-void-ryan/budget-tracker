using System.Net;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Tests.Auth;

public class SessionPurgeTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task AddSessionAsync(User user, TimeSpan expiresIn)
    {
        Db.Add(new Session
        {
            UserId = user.Id,
            TokenHash = SessionTokens.Generate().Hash,
            CreatedAt = Clock.GetUtcNow().AddDays(-30),
            ExpiresAt = Clock.GetUtcNow() + expiresIn,
        });
        await Db.SaveChangesAsync();
    }

    private async Task<User> AddUserAsync()
    {
        var user = new User { Email = $"{Guid.NewGuid():N}@example.com", PasswordHash = "x" };
        Db.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task WaitForAsync(Func<Task<bool>> condition, Action? between = null)
    {
        // Real time passes only while we wait for the purger's task to run; fake time is what
        // moves the timer. Each round advances the fake clock one interval.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the purge");
            between?.Invoke();
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Purging_deletes_expired_sessions_and_keeps_live_ones()
    {
        var user = await AddUserAsync();
        await AddSessionAsync(user, TimeSpan.FromDays(-1));
        await AddSessionAsync(user, TimeSpan.FromSeconds(-1));
        await AddSessionAsync(user, TimeSpan.FromDays(1));

        var purged = await NewSessionService(Db).PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(2, purged);
        Assert.Equal(1, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task A_session_expiring_exactly_now_is_purged_and_one_expiring_a_second_later_is_not()
    {
        var user = await AddUserAsync();
        await AddSessionAsync(user, TimeSpan.Zero);
        await AddSessionAsync(user, TimeSpan.FromSeconds(1));

        await NewSessionService(Db).PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task The_background_purger_clears_expired_sessions_as_time_passes()
    {
        var user = await AddUserAsync();
        await AddSessionAsync(user, TimeSpan.FromDays(-3));
        await AddSessionAsync(user, TimeSpan.FromDays(-2));
        await AddSessionAsync(user, TimeSpan.FromDays(365));

        await WaitForAsync(
            async () => await Db.Sessions.CountAsync(s => s.ExpiresAt <= Clock.GetUtcNow()) == 0,
            between: () => Clock.Advance(TimeSpan.FromMinutes(60)));

        Assert.Equal(1, await Db.Sessions.CountAsync());
    }

    [Fact]
    public async Task A_failed_purge_is_survived_and_the_purger_keeps_running()
    {
        // An exception escaping a BackgroundService stops the host, so one failed purge (say a
        // database restart) must not end the purger or the API with it. The first attempt
        // fails; the purger must still be attempting at later intervals. This asserts on the
        // attempts themselves, not on rows: the application host under test runs its own
        // purger on the same clock and would clear any rows first.
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => Fixture.CreateContext(ConnectionString));
        services.AddScoped(provider =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("database unavailable");
            }

            return new SessionService(
                provider.GetRequiredService<BudgetDbContext>(), Clock, Options.Create(new AuthSessionOptions()));
        });
        await using var provider = services.BuildServiceProvider();
        var purger = new ExpiredSessionPurger(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Clock,
            Options.Create(new AuthSessionOptions()),
            NullLogger<ExpiredSessionPurger>.Instance);

        await purger.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(
                () => Task.FromResult(Volatile.Read(ref attempts) >= 3),
                between: () => Clock.Advance(TimeSpan.FromMinutes(60)));
        }
        finally
        {
            await purger.StopAsync(CancellationToken.None);
        }

        Assert.True(Volatile.Read(ref attempts) >= 3);
    }

    [Fact]
    public async Task Purging_does_not_change_what_authenticates()
    {
        var (_, live) = await CreateUserWithSessionAsync(lifetime: TimeSpan.FromDays(14));
        var (_, expired) = await CreateUserWithSessionAsync(lifetime: TimeSpan.FromDays(1), age: TimeSpan.FromDays(2));

        await NewSessionService(Db).PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", live))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", expired))).StatusCode);
    }
}

public class SessionOptionsValidationTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Theory]
    [InlineData("Sessions:MaxPerUser", "0")]
    [InlineData("Sessions:PurgeIntervalMinutes", "0")]
    [InlineData("Sessions:LifetimeDays", "0")]
    public void Nonsensical_settings_stop_the_application_from_starting(string key, string value)
    {
        // A zero interval would crash the purger's timer at runtime; failing at startup says
        // so up front instead.
        using var factory = new ApiFactory(
            ConnectionString, new Dictionary<string, string?> { [key] = value }, Clock);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(key.Split(':')[1], exception.ToString());
    }
}
