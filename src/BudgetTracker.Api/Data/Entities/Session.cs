namespace BudgetTracker.Api.Data.Entities;

/// <summary>
/// A login session. The bearer token itself is never stored: only its SHA-256 hash, so a
/// database leak does not hand out usable tokens.
/// </summary>
public class Session
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public required byte[] TokenHash { get; init; }

    // Both come from the application's TimeProvider, not the database clock, so tests can
    // move time freely without violating the expires-after-created check.
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
