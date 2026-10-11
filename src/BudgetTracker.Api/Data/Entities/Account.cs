namespace BudgetTracker.Api.Data.Entities;

public enum AccountType
{
    Checking,
    Savings,
    Credit,
}

public class Account
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }

    /// <summary>
    /// Some banks export purchases as positive amounts (common for credit cards).
    /// Import flips the sign when this is set, so stored amounts always follow
    /// one rule: negative means money leaving you.
    /// </summary>
    public bool InvertSign { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
}
