using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

public sealed class SessionService(BudgetDbContext db, TimeProvider time, IOptions<AuthSessionOptions> options)
{
    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateAsync(Guid userId, CancellationToken ct)
    {
        var (token, hash) = SessionTokens.Generate();
        var now = time.GetUtcNow();
        var session = new Session
        {
            UserId = userId,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.LifetimeDays),
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialize session creation per user. Without the lock each concurrent login ranks only
        // the rows it can see, so logins can evict a session another login just kept (ending
        // under the cap) or miss it (ending over it). The lock is on the user's row and is held
        // until commit, so each login sees the previous one's committed result.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", ct);

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        // Keep the newest (cap - 1) other live sessions plus the one just created, and delete
        // everything else of this user's, expired rows included. The new session is excluded
        // from the ranking so it can never be the one evicted, even when timestamps tie.
        var keep = await db.Sessions
            .Where(s => s.UserId == userId && s.Id != session.Id && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Take(options.Value.MaxPerUser - 1)
            .Select(s => s.Id)
            .ToListAsync(ct);

        await db.Sessions
            .Where(s => s.UserId == userId && s.Id != session.Id && !keep.Contains(s.Id))
            .ExecuteDeleteAsync(ct);

        await transaction.CommitAsync(ct);
        return (token, session.ExpiresAt);
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken ct)
    {
        var hash = SessionTokens.Hash(token);
        if (hash is null)
        {
            return false;
        }

        return await db.Sessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Deletes every session that has expired. Expired sessions never authenticate
    /// regardless; this only reclaims the rows.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Sessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }
}
