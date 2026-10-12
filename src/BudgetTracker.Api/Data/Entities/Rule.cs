namespace BudgetTracker.Api.Data.Entities;

public enum RuleMatchType
{
    Contains,
    StartsWith,
    Equals,
}

/// <summary>
/// "When a description matches this pattern, categorize the transaction and/or mark it as a
/// transfer." Rules are tried in position order and the first match wins.
/// </summary>
public class Rule
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }

    /// <summary>The category to assign; null for a rule that only marks transfers.</summary>
    public Guid? CategoryId { get; set; }

    public int Position { get; set; }
    public RuleMatchType MatchType { get; set; }

    /// <summary>Matched literally, ignoring case, spacing and Unicode composition. Never a regex.</summary>
    public required string Pattern { get; set; }

    public bool MarkTransfer { get; set; }
}
