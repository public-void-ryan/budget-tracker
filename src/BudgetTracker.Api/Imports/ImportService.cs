using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Imports;

/// <param name="Amount">What is (or would be) stored: the file's amount with the account's sign setting applied.</param>
/// <param name="FileAmount">Exactly what the file said.</param>
/// <param name="Status">"new" or "duplicate".</param>
public sealed record ImportRowResult(
    int Row, DateOnly PostedOn, string Description, decimal Amount, decimal FileAmount, string Status);

public sealed record ImportSummary(
    bool DryRun,
    bool SignInverted,
    int Total,
    int New,
    int Duplicates,
    int Imported,
    IReadOnlyList<ImportRowResult> Rows);

public enum ImportOutcome
{
    Done,
    AccountNotFound,
    AccountArchived,
}

public sealed record ImportResult(ImportOutcome Outcome, ImportSummary? Summary = null);

public sealed class ImportService(BudgetDbContext db)
{
    /// <summary>
    /// Classifies the rows against what the account already holds and, unless this is a dry run,
    /// stores the new ones, all in one transaction. A dry run takes no lock and writes nothing.
    /// </summary>
    public async Task<ImportResult> RunAsync(
        Guid userId, Guid accountId, IReadOnlyList<ParsedRow> rows, bool dryRun, CancellationToken ct)
    {
        var numbered = ImportDedup.Number(rows);

        if (dryRun)
        {
            var account = await LoadAccountAsync(userId, accountId, ct);
            return account is null
                ? new ImportResult(ImportOutcome.AccountNotFound)
                : Refused(account) ?? new ImportResult(
                    ImportOutcome.Done, Summarize(account, await ClassifyAsync(accountId, numbered, ct), dryRun: true, imported: 0));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialize imports into one account. Two simultaneous imports of overlapping files would
        // each see the other's rows as missing and both try to insert them; the lock makes the
        // second wait and then find them already there. Scoped to the caller's own account so a
        // guessed id cannot be used to hold someone else's row locked.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM accounts WHERE id = {accountId} AND user_id = {userId} FOR UPDATE", ct);

        // Read after taking the lock: the account may have been archived or deleted while this
        // request was waiting for it.
        var locked = await LoadAccountAsync(userId, accountId, ct);
        if (locked is null)
        {
            return new ImportResult(ImportOutcome.AccountNotFound);
        }

        if (Refused(locked) is { } refused)
        {
            return refused;
        }

        var classified = await ClassifyAsync(accountId, numbered, ct);
        var factor = locked.InvertSign ? -1m : 1m;

        var created = classified
            .Where(c => c.Status == RowStatus.New)
            .Select(c => new Transaction
            {
                UserId = userId,
                AccountId = accountId,
                PostedOn = c.Hashed.Row.Date,
                Amount = c.Hashed.Row.Amount * factor,
                Description = c.Hashed.Row.Description,
                ImportHash = c.Hashed.Hash,
                ImportSeq = c.Hashed.Sequence,
            })
            .ToList();

        db.Transactions.AddRange(created);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportResult(ImportOutcome.Done, Summarize(locked, classified, dryRun: false, imported: created.Count));
    }

    private Task<Account?> LoadAccountAsync(Guid userId, Guid accountId, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId && a.Id == accountId, ct);

    private static ImportResult? Refused(Account account) =>
        account.ArchivedAt is not null ? new ImportResult(ImportOutcome.AccountArchived) : null;

    private async Task<IReadOnlyList<ClassifiedRow>> ClassifyAsync(
        Guid accountId, IReadOnlyList<HashedRow> numbered, CancellationToken ct)
    {
        var hashes = numbered.Select(r => r.Hash).Distinct().ToList();

        var stored = await db.Transactions
            .AsNoTracking()
            .Where(t => t.AccountId == accountId && t.ImportHash != null && hashes.Contains(t.ImportHash))
            .Select(t => new { t.ImportHash, t.ImportSeq })
            .ToListAsync(ct);

        var existing = stored
            .Where(t => t.ImportSeq is not null)
            .Select(t => (t.ImportHash!, t.ImportSeq!.Value))
            .ToHashSet();

        return ImportDedup.Classify(numbered, existing);
    }

    private static ImportSummary Summarize(Account account, IReadOnlyList<ClassifiedRow> classified, bool dryRun, int imported)
    {
        var factor = account.InvertSign ? -1m : 1m;
        var rows = classified
            .Select(c => new ImportRowResult(
                c.Hashed.Row.Row,
                c.Hashed.Row.Date,
                c.Hashed.Row.Description,
                c.Hashed.Row.Amount * factor,
                c.Hashed.Row.Amount,
                c.Status == RowStatus.New ? "new" : "duplicate"))
            .ToList();

        return new ImportSummary(
            dryRun,
            account.InvertSign,
            rows.Count,
            rows.Count(r => r.Status == "new"),
            rows.Count(r => r.Status == "duplicate"),
            imported,
            rows);
    }
}
