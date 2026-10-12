using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Rules;

/// <param name="Matched">How many candidate transactions this rule was the first to claim.</param>
public sealed record RuleHit(Guid RuleId, string Pattern, int Matched);

/// <param name="Examined">Transactions that could be filled in: no category, not a transfer, in an open account.</param>
/// <param name="Categorized">Transactions that were (or in a dry run would be) given a category.</param>
/// <param name="MarkedAsTransfer">Transactions that were (or would be) marked as transfers.</param>
/// <param name="SkippedArchived">Candidates in archived accounts that a rule matches but are left alone, since archived accounts are frozen.</param>
public sealed record ApplyResult(
    bool DryRun,
    int Examined,
    int Categorized,
    int MarkedAsTransfer,
    int SkippedArchived,
    IReadOnlyList<RuleHit> ByRule);

/// <summary>
/// Applies a user's rules to transactions that already exist. It only ever fills blanks: a
/// transaction that has a category, or is marked as a transfer, is never touched, so nothing a
/// person decided is overwritten.
/// </summary>
public sealed class RuleApplier(BudgetDbContext db)
{
    private const int BatchSize = 1_000;

    public async Task<ApplyResult> ApplyAsync(Guid userId, bool dryRun, CancellationToken ct)
    {
        await using var transaction = dryRun ? null : await db.Database.BeginTransactionAsync(ct);

        // One application at a time per user, so two simultaneous applies cannot interleave and
        // a rule edit cannot land halfway through one.
        if (!dryRun)
        {
            await db.AcquireUserLockAsync(userId, ct);
        }

        var matcher = new RuleMatcher(await db.Rules
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => new RuleDefinition(r.Id, r.Position, r.MatchType, r.Pattern, r.CategoryId, r.MarkTransfer))
            .ToListAsync(ct));

        var hits = new Dictionary<Guid, int>();
        int examined = 0, categorized = 0, marked = 0, skipped = 0;

        if (matcher.Count > 0)
        {
            IQueryable<Transaction> Candidates(bool archived) => db.Transactions
                .AsNoTracking()
                .Where(t => t.UserId == userId && t.CategoryId == null && !t.IsTransfer)
                .Where(t => db.Accounts.Any(a => a.UserId == userId && a.Id == t.AccountId && a.ArchivedAt != null) == archived);

            await ScanAsync(Candidates(archived: false), async batch =>
            {
                examined += batch.Count;

                var groups = new Dictionary<(Guid? Category, bool Transfer), List<Guid>>();
                foreach (var row in batch)
                {
                    if (matcher.FirstMatch(row.Description) is not { } rule)
                    {
                        continue;
                    }

                    hits[rule.Id] = hits.GetValueOrDefault(rule.Id) + 1;
                    var key = (rule.CategoryId, rule.MarkTransfer);
                    if (!groups.TryGetValue(key, out var ids))
                    {
                        groups[key] = ids = [];
                    }

                    ids.Add(row.Id);
                }

                foreach (var ((category, transfer), ids) in groups)
                {
                    // In a real run only rows that are still blank are changed, whatever happened
                    // since they were read; the count is of rows actually changed.
                    var changed = dryRun
                        ? ids.Count
                        : await db.Transactions
                            .Where(t => t.UserId == userId && ids.Contains(t.Id) && t.CategoryId == null && !t.IsTransfer)
                            .ExecuteUpdateAsync(set => set
                                .SetProperty(t => t.CategoryId, category)
                                .SetProperty(t => t.IsTransfer, transfer), ct);

                    categorized += category is null ? 0 : changed;
                    marked += transfer ? changed : 0;
                }
            }, ct);

            // Archived accounts are frozen: matched but left alone, and said so.
            await ScanAsync(Candidates(archived: true), batch =>
            {
                skipped += batch.Count(row => matcher.FirstMatch(row.Description) is not null);
                return Task.CompletedTask;
            }, ct);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        var patterns = matcher.InOrder.ToDictionary(r => r.Id, r => r.Pattern);
        var byRule = matcher.InOrder
            .Where(r => hits.ContainsKey(r.Id))
            .Select(r => new RuleHit(r.Id, patterns[r.Id], hits[r.Id]))
            .ToList();

        return new ApplyResult(dryRun, examined, categorized, marked, skipped, byRule);
    }

    private sealed record Row(Guid Id, string Description);

    /// <summary>Walks a query in id order, a batch at a time, so memory stays bounded however many rows there are.</summary>
    private static async Task ScanAsync(IQueryable<Transaction> source, Func<List<Row>, Task> handle, CancellationToken ct)
    {
        var last = Guid.Empty;
        while (true)
        {
            var after = last;
            var batch = await source
                .Where(t => t.Id.CompareTo(after) > 0)
                .OrderBy(t => t.Id)
                .Take(BatchSize)
                .Select(t => new Row(t.Id, t.Description))
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                return;
            }

            await handle(batch);
            last = batch[^1].Id;
        }
    }
}
