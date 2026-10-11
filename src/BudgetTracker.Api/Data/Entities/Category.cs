namespace BudgetTracker.Api.Data.Entities;

public class Category
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public required string Name { get; set; }
}
