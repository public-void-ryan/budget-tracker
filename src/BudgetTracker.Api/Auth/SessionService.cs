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

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
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
}
