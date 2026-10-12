using System.Net;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Auth;

public class SessionCapTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private async Task<User> AddUserAsync()
    {
        var user = new User { Email = $"{Guid.NewGuid():N}@example.com", PasswordHash = "not-a-real-hash" };
        Db.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task<List<string>> LoginRepeatedlyAsync(User user, int times)
    {
        var service = NewSessionService(Db);
        var tokens = new List<string>();
        for (var i = 0; i < times; i++)
        {
            tokens.Add((await service.CreateAsync(user.Id, CancellationToken.None)).Token);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        return tokens;
    }

    [Fact]
    public async Task Up_to_ten_sessions_are_all_kept()
    {
        var user = await AddUserAsync();

        await LoginRepeatedlyAsync(user, 10);

        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
    }

    [Fact]
    public async Task The_eleventh_login_evicts_the_oldest_session_only()
    {
        var user = await AddUserAsync();

        var tokens = await LoginRepeatedlyAsync(user, 11);

        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", tokens[0]))).StatusCode);
        foreach (var survivor in tokens.Skip(1))
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", survivor))).StatusCode);
        }
    }

    [Fact]
    public async Task Evicting_one_users_sessions_never_touches_another_users()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        await LoginRepeatedlyAsync(bob, 10);

        await LoginRepeatedlyAsync(alice, 15);

        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == alice.Id));
        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == bob.Id));
    }

    [Fact]
    public async Task The_newest_session_is_never_the_one_evicted_even_when_timestamps_tie()
    {
        // The clock is frozen, so every session has the same created_at; a naive "delete the
        // lowest ranked" could evict the session that was just handed back to the caller.
        var user = await AddUserAsync();
        var service = NewSessionService(Db);

        for (var i = 0; i < 12; i++)
        {
            var (token, _) = await service.CreateAsync(user.Id, CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);
        }

        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
    }

    [Fact]
    public async Task Expired_sessions_do_not_count_toward_the_cap_and_are_cleaned_up_on_login()
    {
        var user = await AddUserAsync();
        var live = await LoginRepeatedlyAsync(user, 9);
        for (var i = 0; i < 5; i++)
        {
            Db.Add(new Session
            {
                UserId = user.Id,
                TokenHash = SessionTokens.Generate().Hash,
                CreatedAt = Clock.GetUtcNow().AddDays(-20),
                ExpiresAt = Clock.GetUtcNow().AddDays(-6),
            });
        }

        await Db.SaveChangesAsync();

        await NewSessionService(Db).CreateAsync(user.Id, CancellationToken.None);

        // 9 live + the new one = 10; none of the 9 was evicted to make room, and the 5 expired
        // rows are gone.
        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
        Assert.Equal(0, await Db.Sessions.CountAsync(s => s.ExpiresAt <= Clock.GetUtcNow()));
        foreach (var token in live)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Simultaneous_logins_never_leave_more_than_the_cap()
    {
        // Without a per-user lock these transactions rank only the rows they can see, so the
        // total lands off the cap (in practice under it); with the lock it is exactly the cap.
        var user = await AddUserAsync();
        using var start = new ManualResetEventSlim(false);

        var logins = Enumerable.Range(0, 25).Select(_ => Task.Run(async () =>
        {
            await using var context = Fixture.CreateContext(ConnectionString);
            start.Wait();
            await NewSessionService(context).CreateAsync(user.Id, CancellationToken.None);
        })).ToList();

        start.Set();
        await Task.WhenAll(logins);

        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
    }

    [Fact]
    public async Task Over_http_the_eleventh_login_signs_the_oldest_device_out()
    {
        var (user, firstToken) = await CreateUserWithSessionAsync(password: GoodPassword);
        var others = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var login = await PostAsync("/auth/login", new { email = user.Email, password = GoodPassword });
            others.Add((await ReadJsonAsync(login)).GetProperty("token").GetString()!);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", firstToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", others[^1]))).StatusCode);
        Assert.Equal(10, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
    }
}

public class ConfiguredCapTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["Sessions:MaxPerUser"] = "2" };

    [Fact]
    public async Task The_cap_comes_from_configuration()
    {
        var (user, first) = await CreateUserWithSessionAsync(password: GoodPassword);
        var tokens = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var login = await PostAsync("/auth/login", new { email = user.Email, password = GoodPassword });
            tokens.Add((await ReadJsonAsync(login)).GetProperty("token").GetString()!);
        }

        Assert.Equal(2, await Db.Sessions.CountAsync(s => s.UserId == user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(Get("/me", first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", tokens[0]))).StatusCode);
    }

    [Fact]
    public async Task A_cap_of_one_still_keeps_the_session_just_created()
    {
        await using var context = Fixture.CreateContext(ConnectionString);
        var user = new User { Email = "one@example.com", PasswordHash = "x" };
        context.Add(user);
        await context.SaveChangesAsync();
        var service = NewSessionService(context, new AuthSessionOptions { MaxPerUser = 1 });

        await service.CreateAsync(user.Id, CancellationToken.None);
        var (token, _) = await service.CreateAsync(user.Id, CancellationToken.None);

        Assert.Equal(1, await context.Sessions.CountAsync());
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(Get("/me", token))).StatusCode);
    }
}
