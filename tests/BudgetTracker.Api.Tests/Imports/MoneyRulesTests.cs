using BudgetTracker.Api.Validation;

namespace BudgetTracker.Api.Tests.Imports;

public class MoneyRulesTests
{
    [Theory]
    [InlineData("0.0001")]
    [InlineData("-0.0001")]
    [InlineData("12.5")]
    [InlineData("999999999999999.9999")]
    [InlineData("-999999999999999.9999")]
    public void Acceptable_amounts_have_no_problem(string amount)
    {
        Assert.Null(MoneyRules.Check(decimal.Parse(amount)));
    }

    [Theory]
    [InlineData("0", "amount.zero")]
    [InlineData("0.0000", "amount.zero")]
    [InlineData("0.00001", "amount.too_precise")]
    [InlineData("-12.34567", "amount.too_precise")]
    [InlineData("1000000000000000", "amount.out_of_range")]
    [InlineData("-1000000000000000", "amount.out_of_range")]
    public void Unacceptable_amounts_say_why(string amount, string code)
    {
        Assert.Equal(code, MoneyRules.Check(decimal.Parse(amount))?.Code);
    }

    [Fact]
    public void An_amount_written_with_extra_trailing_zeros_is_not_too_precise()
    {
        Assert.Null(MoneyRules.Check(12.50000m));
    }
}
