namespace BudgetTracker.Api.Data.Entities;

public class Transaction
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    // Denormalized on purpose: composite foreign keys on (UserId, AccountId) and
    // (UserId, CategoryId) make it impossible for the database to link a
    // transaction to another user's account or category.
    public required Guid UserId { get; init; }
    public required Guid AccountId { get; init; }
    public Guid? CategoryId { get; set; }

    public DateOnly PostedOn { get; set; }

    /// <summary>Signed; negative is money leaving the account. Never zero.</summary>
    public decimal Amount { get; set; }

    public required string Description { get; set; }

    /// <summary>Moves between your own accounts; excluded from spending and budgets.</summary>
    public bool IsTransfer { get; set; }

    // CSV import dedup. The hash identifies a row's content; the sequence number
    // distinguishes genuinely identical rows (two coffees, same shop, same day)
    // within one file. Both are null for manually entered transactions.
    public string? ImportHash { get; set; }
    public short? ImportSeq { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
