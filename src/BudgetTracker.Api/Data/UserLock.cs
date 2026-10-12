using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Data;

public static class UserLock
{
    /// <summary>
    /// Locks the user's row until the surrounding transaction ends, so work that must not
    /// interleave for one user (creating sessions or rules, reordering rules) runs one request at
    /// a time. Without it, concurrent transactions each count what they can see, and limits and
    /// numbering drift. Call it inside a transaction, before reading what the work depends on.
    /// </summary>
    public static async Task AcquireUserLockAsync(this BudgetDbContext db, Guid userId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", ct);
}
