using System.ComponentModel.DataAnnotations;

namespace BudgetTracker.Api.Rules;

public sealed class RuleOptions
{
    public const string Section = "Rules";

    /// <summary>
    /// Bounds the work of matching, which runs in memory over a user's whole rule list for every
    /// transaction it is applied to.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxPerUser { get; set; } = 500;
}
