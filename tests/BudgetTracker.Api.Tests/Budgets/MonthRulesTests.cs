using BudgetTracker.Api.Validation;

namespace BudgetTracker.Api.Tests.Budgets;

public class MonthRulesTests
{
    [Theory]
    [InlineData("2026-10", 2026, 10)]
    [InlineData("2026-01", 2026, 1)]
    [InlineData("2026-12", 2026, 12)]
    [InlineData("1999-02", 1999, 2)]
    [InlineData("0001-01", 1, 1)]
    [InlineData("9999-12", 9999, 12)]
    public void A_real_month_becomes_its_first_day(string raw, int year, int month)
    {
        var (parsed, error) = MonthRules.Parse(raw, "month");

        Assert.Null(error);
        Assert.Equal(new DateOnly(year, month, 1), parsed);
    }

    [Theory]
    [InlineData(null, "month.required")]
    [InlineData("", "month.required")]
    [InlineData("   ", "month.required")]
    [InlineData("2026-13", "month.invalid")]
    [InlineData("2026-00", "month.invalid")]
    [InlineData("2026-1", "month.invalid")]
    [InlineData("202610", "month.invalid")]
    [InlineData("2026-10-01", "month.invalid")]
    [InlineData("2026/10", "month.invalid")]
    [InlineData("10-2026", "month.invalid")]
    [InlineData("26-10", "month.invalid")]
    [InlineData("0000-01", "month.invalid")]
    [InlineData(" 2026-10", "month.invalid")]
    [InlineData("2026-10 ", "month.invalid")]
    [InlineData("October 2026", "month.invalid")]
    [InlineData("2026-1\n", "month.invalid")]
    public void Anything_else_is_reported_not_guessed(string? raw, string code)
    {
        var (parsed, error) = MonthRules.Parse(raw, "month");

        Assert.Null(parsed);
        Assert.Equal(("month", code), (error!.Field, error.Code));
    }

    [Fact]
    public void The_field_name_is_used_in_the_error()
    {
        var (_, error) = MonthRules.Parse("nope", "from");

        Assert.Equal(("from", "from.invalid"), (error!.Field, error.Code));
    }

    [Fact]
    public void A_month_formats_back_to_what_was_parsed()
    {
        Assert.Equal("2026-03", MonthRules.Format(new DateOnly(2026, 3, 1)));
    }
}
