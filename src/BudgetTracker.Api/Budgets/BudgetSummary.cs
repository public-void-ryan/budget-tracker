namespace BudgetTracker.Api.Budgets;

/// <param name="Budgeted">The planned amount; null when no budget was set for the category that month.</param>
/// <param name="Spent">Net outflow: refunds reduce it, and an income category shows a negative number.</param>
/// <param name="Remaining">Budgeted minus spent; null when there is no budget.</param>
/// <param name="Over">Spent more than budgeted. Exactly on budget is not over.</param>
public sealed record SummaryCategory(
    Guid CategoryId, string CategoryName, decimal? Budgeted, decimal Spent, decimal? Remaining, bool Over);

/// <param name="Budgeted">The sum of the budgets that exist.</param>
/// <param name="Spent">All net outflow in the month, categorized or not: the sum of every row above plus uncategorized.</param>
public sealed record SummaryTotals(decimal Budgeted, decimal Spent);

public sealed record BudgetSummary(
    string Month,
    IReadOnlyList<SummaryCategory> Categories,
    decimal UncategorizedSpent,
    SummaryTotals Totals);

public sealed record CategoryRef(Guid Id, string Name);

/// <summary>
/// The arithmetic of budget versus actual, with no database in it. The caller supplies the
/// month's budgets and the signed sum of its non-transfer transactions per category; this decides
/// what appears, what is "spent", and makes the totals add up to the rows.
/// </summary>
public static class BudgetSummaryBuilder
{
    /// <param name="categories">Every category that might appear, in the order to show them.</param>
    /// <param name="budgeted">Planned amount per category, for those that have a budget this month.</param>
    /// <param name="netAmounts">The signed sum of the month's non-transfer transactions per category, for categories with any.</param>
    /// <param name="uncategorizedNet">The signed sum of the month's non-transfer transactions that have no category.</param>
    public static BudgetSummary Build(
        string month,
        IEnumerable<CategoryRef> categories,
        IReadOnlyDictionary<Guid, decimal> budgeted,
        IReadOnlyDictionary<Guid, decimal> netAmounts,
        decimal uncategorizedNet)
    {
        var rows = new List<SummaryCategory>();

        foreach (var category in categories)
        {
            var hasBudget = budgeted.TryGetValue(category.Id, out var planned);
            var hasActivity = netAmounts.TryGetValue(category.Id, out var net);

            // A category with neither a budget nor any transaction that month has nothing to say.
            if (!hasBudget && !hasActivity)
            {
                continue;
            }

            // Money out is negative in the data; "spent" is the positive reading of it.
            var spent = -net;
            rows.Add(new SummaryCategory(
                category.Id,
                category.Name,
                hasBudget ? planned : null,
                spent,
                hasBudget ? planned - spent : null,
                hasBudget && spent > planned));
        }

        var uncategorizedSpent = -uncategorizedNet;
        var totals = new SummaryTotals(
            rows.Sum(r => r.Budgeted ?? 0m),
            rows.Sum(r => r.Spent) + uncategorizedSpent);

        return new BudgetSummary(month, rows, uncategorizedSpent, totals);
    }
}
