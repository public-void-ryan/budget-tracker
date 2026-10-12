using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Validation;

namespace BudgetTracker.Api.Tests.Rules;

public class EnumRulesTests
{
    [Theory]
    [InlineData("Contains", RuleMatchType.Contains)]
    [InlineData("StartsWith", RuleMatchType.StartsWith)]
    [InlineData("Equals", RuleMatchType.Equals)]
    [InlineData("  Equals  ", RuleMatchType.Equals)]
    public void The_exact_names_are_accepted_after_trimming(string raw, RuleMatchType expected)
    {
        var (value, errors) = EnumRules.ParseExact<RuleMatchType>(raw, "matchType");

        Assert.Empty(errors);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null, "matchType.required")]
    [InlineData("", "matchType.required")]
    [InlineData("   ", "matchType.required")]
    [InlineData("contains", "matchType.invalid")]
    [InlineData("CONTAINS", "matchType.invalid")]
    [InlineData("Regex", "matchType.invalid")]
    [InlineData("0", "matchType.invalid")]
    [InlineData("1", "matchType.invalid")]
    [InlineData("99", "matchType.invalid")]
    [InlineData("Contains, Equals", "matchType.invalid")]
    [InlineData("Contains|Equals", "matchType.invalid")]
    [InlineData("Contains,", "matchType.invalid")]
    public void Anything_else_is_refused_not_guessed(string? raw, string code)
    {
        var (_, errors) = EnumRules.ParseExact<RuleMatchType>(raw, "matchType");

        var error = Assert.Single(errors);
        Assert.Equal(("matchType", code), (error.Field, error.Code));
    }

    [Fact]
    public void The_message_lists_what_is_allowed()
    {
        var (_, errors) = EnumRules.ParseExact<RuleMatchType>("nope", "matchType");

        Assert.Equal("MatchType must be one of: Contains, StartsWith, Equals.", errors.Single().Message);
    }
}
