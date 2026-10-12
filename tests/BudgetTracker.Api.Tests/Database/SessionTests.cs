using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

public class SessionTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task Token_hash_is_unique()
    {
        var user = await AddUserAsync();
        var hash = new byte[32];
        Db.Add(NewSession(user.Id, hash));
        await Db.SaveChangesAsync();

        await AssertViolationAsync(UniqueViolation, "ix_sessions_token_hash", async () =>
        {
            Db.Add(NewSession(user.Id, hash));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Token_hash_must_be_a_sha256_digest()
    {
        var user = await AddUserAsync();

        await AssertViolationAsync(CheckViolation, "ck_sessions_token_hash_length", async () =>
        {
            Db.Add(NewSession(user.Id, tokenHash: new byte[16]));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Session_must_expire_after_it_was_created()
    {
        var user = await AddUserAsync();

        await AssertViolationAsync(CheckViolation, "ck_sessions_expires_after_created", async () =>
        {
            Db.Add(NewSession(user.Id, lifetime: TimeSpan.Zero));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Session_needs_an_existing_user()
    {
        await AssertViolationAsync(ForeignKeyViolation, "fk_sessions_users_user_id", async () =>
        {
            Db.Add(NewSession(Guid.NewGuid()));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Timestamps_round_trip_exactly()
    {
        // Expiry decisions compare these values, so they must survive storage unchanged.
        var user = await AddUserAsync();
        var created = new DateTimeOffset(2026, 10, 1, 12, 30, 45, 123, TimeSpan.Zero);
        var session = NewSession(user.Id, createdAt: created, lifetime: TimeSpan.FromDays(14));
        Db.Add(session);
        await Db.SaveChangesAsync();

        var stored = await Db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);

        Assert.Equal(created, stored.CreatedAt);
        Assert.Equal(created.AddDays(14), stored.ExpiresAt);
    }

    [Fact]
    public async Task Deleting_a_user_deletes_their_sessions_and_only_theirs()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        await AddSessionAsync(alice);
        await AddSessionAsync(alice);
        var bobSession = await AddSessionAsync(bob);

        await Db.Users.Where(u => u.Id == alice.Id).ExecuteDeleteAsync();

        var remaining = await Db.Sessions.AsNoTracking().ToListAsync();
        Assert.Equal(bobSession.Id, Assert.Single(remaining).Id);
    }
}
