namespace BudgetTracker.Api.Budgets;

/// <param name="Budgeted">The planned amount; null when no budget was set for the category that month.</param>
/// <param name="Spent">Net outflow: refunds reduce it. Negative only for a budgeted category that took in more than it paid out.</param>
/// <param name="Remaining">Budgeted minus spent; null when there is no budget.</param>
/// <param name="Over">Spent more than budgeted. Exactly on budget is not over.</param>
public sealed record SummaryCategory(
    Guid CategoryId, string CategoryName, decimal? Budgeted, decimal Spent, decimal? Remaining, bool Over);

/// <param name="Received">Net inflow for a category with no budget that took in more than it paid out (salary, say).</param>
public sealed record IncomeCategory(Guid CategoryId, string CategoryName, decimal Received);

/// <param name="Budgeted">The sum of the budgets that exist.</param>
/// <param name="Spent">All net outflow in the month: every expense row plus uncategorized.</param>
/// <param name="Remaining">Budgeted minus spent.</param>
/// <param name="Received">All net inflow in the month: every income row plus uncategorized.</param>
/// <param name="Net">Received minus spent.</param>
public sealed record SummaryTotals(decimal Budgeted, decimal Spent, decimal Remaining, decimal Received, decimal Net);

public sealed record BudgetSummary(
    string Month,
    IReadOnlyList<SummaryCategory> Categories,
    IReadOnlyList<IncomeCategory> Income,
    decimal UncategorizedSpent,
    decimal UncategorizedReceived,
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
    /// <param name="uncategorizedOut">Money out on the month's non-transfer transactions that have no category, as a positive number.</param>
    /// <param name="uncategorizedIn">Money in on those same transactions, as a positive number. Kept apart from the money out: unrelated items netted together would hide both.</param>
    public static BudgetSummary Build(
        string month,
        IEnumerable<CategoryRef> categories,
        IReadOnlyDictionary<Guid, decimal> budgeted,
        IReadOnlyDictionary<Guid, decimal> netAmounts,
        decimal uncategorizedOut,
        decimal uncategorizedIn)
    {
        var rows = new List<SummaryCategory>();
        var income = new List<IncomeCategory>();

        foreach (var category in categories)
        {
            var hasBudget = budgeted.TryGetValue(category.Id, out var planned);
            var hasActivity = netAmounts.TryGetValue(category.Id, out var net);

            // A category with neither a budget nor any transaction that month has nothing to say.
            if (!hasBudget && !hasActivity)
            {
                continue;
            }

            // Money in with nothing planned for it is income, not negative spending. A budgeted
            // category stays an expense row even if refunds outweigh purchases.
            if (!hasBudget && net > 0)
            {
                income.Add(new IncomeCategory(category.Id, category.Name, net));
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

        var uncategorizedSpent = uncategorizedOut;
        var uncategorizedReceived = uncategorizedIn;

        var budgetedTotal = rows.Sum(r => r.Budgeted ?? 0m);
        var spentTotal = rows.Sum(r => r.Spent) + uncategorizedSpent;
        var receivedTotal = income.Sum(i => i.Received) + uncategorizedReceived;
        var totals = new SummaryTotals(budgetedTotal, spentTotal, budgetedTotal - spentTotal, receivedTotal, receivedTotal - spentTotal);

        return new BudgetSummary(month, rows, income, uncategorizedSpent, uncategorizedReceived, totals);
    }
}
