namespace BudgetTracker.Api.Data.Entities;

public class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Email { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
