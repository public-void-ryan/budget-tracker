namespace BudgetTracker.Api.Data.Entities;

public class Budget
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public required Guid CategoryId { get; init; }

    /// <summary>First day of the budgeted month.</summary>
    public DateOnly Month { get; set; }

    public decimal Amount { get; set; }
}
