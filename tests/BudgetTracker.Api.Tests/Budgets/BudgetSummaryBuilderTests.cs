using BudgetTracker.Api.Budgets;

namespace BudgetTracker.Api.Tests.Budgets;

public class BudgetSummaryBuilderTests
{
    private static readonly CategoryRef Rent = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Rent");
    private static readonly CategoryRef Dining = new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "Dining");
    private static readonly CategoryRef Salary = new(Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "Salary");
    private static readonly CategoryRef Unused = new(Guid.Parse("00000000-0000-0000-0000-0000000000a4"), "Unused");

    private static Dictionary<Guid, decimal> D(params (CategoryRef Category, decimal Amount)[] items) =>
        items.ToDictionary(i => i.Category.Id, i => i.Amount);

    private static BudgetSummary Build(
        CategoryRef[] categories, Dictionary<Guid, decimal>? budgeted = null, Dictionary<Guid, decimal>? net = null,
        decimal uncategorizedNet = 0m) =>
        BudgetSummaryBuilder.Build("2026-10", categories, budgeted ?? [], net ?? [], uncategorizedNet);

    [Fact]
    public void A_category_under_budget_shows_what_is_left()
    {
        var summary = Build([Dining], D((Dining, 200m)), D((Dining, -150m)));

        var row = Assert.Single(summary.Categories);
        Assert.Equal((200m, 150m, 50m, false), (row.Budgeted, row.Spent, row.Remaining, row.Over));
    }

    [Fact]
    public void A_category_over_budget_is_flagged_and_its_remainder_is_negative()
    {
        var row = Assert.Single(Build([Dining], D((Dining, 200m)), D((Dining, -230m))).Categories);

        Assert.Equal((230m, -30m, true), (row.Spent, row.Remaining, row.Over));
    }

    [Theory]
    [InlineData(200.0000, 200.0000, false)]   // exactly on budget is not over
    [InlineData(200.0000, 200.0001, true)]    // a hundredth of a cent over is
    [InlineData(200.0000, 199.9999, false)]
    public void Over_means_strictly_more_than_the_budget(double budget, double spent, bool expectedOver)
    {
        var row = Assert.Single(Build([Dining], D((Dining, (decimal)budget)), D((Dining, -(decimal)spent))).Categories);

        Assert.Equal(expectedOver, row.Over);
    }

    [Fact]
    public void Spending_with_no_budget_has_no_budgeted_or_remaining_figure_and_is_never_over()
    {
        var row = Assert.Single(Build([Dining], net: D((Dining, -40m))).Categories);

        Assert.Equal((null, 40m, null, false), (row.Budgeted, row.Spent, row.Remaining, row.Over));
    }

    [Fact]
    public void A_budget_with_no_spending_is_all_remaining()
    {
        var row = Assert.Single(Build([Rent], D((Rent, 1000m))).Categories);

        Assert.Equal((1000m, 0m, 1000m, false), (row.Budgeted, row.Spent, row.Remaining, row.Over));
    }

    [Fact]
    public void A_refund_reduces_what_was_spent()
    {
        // Spent 150 and 80, refunded 30: the net outflow is 200.
        var row = Assert.Single(Build([Dining], D((Dining, 200m)), D((Dining, -150m - 80m + 30m))).Categories);

        Assert.Equal((200m, 0m, false), (row.Spent, row.Remaining, row.Over));
    }

    [Fact]
    public void Income_in_a_category_shows_as_negative_spending()
    {
        var row = Assert.Single(Build([Salary], net: D((Salary, 2500m))).Categories);

        Assert.Equal(-2500m, row.Spent);
    }

    [Fact]
    public void A_category_whose_transactions_cancel_out_still_appears_with_zero_spent()
    {
        var row = Assert.Single(Build([Dining], net: D((Dining, 0m))).Categories);

        Assert.Equal(0m, row.Spent);
    }

    [Fact]
    public void A_category_with_no_budget_and_no_activity_is_left_out()
    {
        var summary = Build([Rent, Unused], D((Rent, 1000m)));

        Assert.Equal([Rent.Id], summary.Categories.Select(c => c.CategoryId).ToArray());
    }

    [Fact]
    public void The_order_of_the_categories_given_is_the_order_returned()
    {
        var summary = Build([Salary, Dining, Rent], D((Rent, 1m), (Dining, 1m), (Salary, 1m)));

        Assert.Equal(["Salary", "Dining", "Rent"], summary.Categories.Select(c => c.CategoryName).ToArray());
    }

    [Fact]
    public void Uncategorized_spending_is_reported_as_a_positive_figure()
    {
        var summary = Build([], uncategorizedNet: -25m);

        Assert.Equal(25m, summary.UncategorizedSpent);
        Assert.Empty(summary.Categories);
    }

    [Fact]
    public void The_totals_add_up_to_the_rows_and_the_uncategorized_figure()
    {
        var summary = Build(
            [Dining, Rent, Salary],
            D((Rent, 1000m), (Dining, 200m)),
            D((Rent, -1000m), (Dining, -230m), (Salary, 2500m)),
            uncategorizedNet: -25m);

        Assert.Equal(1200m, summary.Totals.Budgeted);
        Assert.Equal(summary.Categories.Sum(c => c.Spent) + summary.UncategorizedSpent, summary.Totals.Spent);
        Assert.Equal(1000m + 230m - 2500m + 25m, summary.Totals.Spent);
    }

    [Fact]
    public void Totals_budgeted_counts_only_budgets_that_exist()
    {
        var summary = Build([Rent, Dining], D((Rent, 1000m)), D((Dining, -50m)));

        Assert.Equal(1000m, summary.Totals.Budgeted);
    }

    [Fact]
    public void Nothing_at_all_is_a_summary_of_zeros()
    {
        var summary = Build([]);

        Assert.Equal(("2026-10", 0m, 0m, 0m), (summary.Month, summary.UncategorizedSpent, summary.Totals.Budgeted, summary.Totals.Spent));
        Assert.Empty(summary.Categories);
    }

    [Fact]
    public void Four_decimal_amounts_add_exactly()
    {
        var summary = Build([Dining], D((Dining, 0.3000m)), D((Dining, -(0.1000m + 0.2000m))));

        var row = Assert.Single(summary.Categories);
        Assert.Equal((0.3000m, 0m, false), (row.Spent, row.Remaining, row.Over));
    }
}
