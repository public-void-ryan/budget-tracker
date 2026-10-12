using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Text;

namespace BudgetTracker.Api.Rules;

/// <summary>A rule as the matcher sees it, detached from the database.</summary>
public sealed record RuleDefinition(
    Guid Id, int Position, RuleMatchType MatchType, string Pattern, Guid? CategoryId, bool MarkTransfer);

/// <summary>
/// Finds the first rule that claims a description. Built once from a user's rules (patterns are
/// normalized up front) and then asked about many descriptions, so applying rules to thousands
/// of transactions does not repeat that work. Pure: no database, no clock.
///
/// Rules are tried by position, and rules at the same position by id. Positions are assigned
/// when a rule is created, so ties are rare; among them the id decides. Version 7 ids begin with
/// a millisecond timestamp, so that is creation order, except between two ids made within the
/// same millisecond, whose order is arbitrary but still the same everywhere.
/// </summary>
public sealed class RuleMatcher
{
    private readonly (RuleDefinition Rule, string Pattern)[] _ordered;

    public RuleMatcher(IEnumerable<RuleDefinition> rules)
    {
        _ordered = [.. rules
            .OrderBy(r => r.Position)
            .ThenBy(r => r.Id)
            .Select(r => (r, DescriptionText.NormalizeForMatching(r.Pattern)))];
    }

    public int Count => _ordered.Length;

    /// <summary>The rules in the order they are tried.</summary>
    public IEnumerable<RuleDefinition> InOrder => _ordered.Select(o => o.Rule);

    public RuleDefinition? FirstMatch(string description)
    {
        var text = DescriptionText.NormalizeForMatching(description);

        foreach (var (rule, pattern) in _ordered)
        {
            var matches = rule.MatchType switch
            {
                RuleMatchType.Contains => text.Contains(pattern, StringComparison.Ordinal),
                RuleMatchType.StartsWith => text.StartsWith(pattern, StringComparison.Ordinal),
                RuleMatchType.Equals => text == pattern,
                _ => false,
            };

            if (matches)
            {
                return rule;
            }
        }

        return null;
    }
}
