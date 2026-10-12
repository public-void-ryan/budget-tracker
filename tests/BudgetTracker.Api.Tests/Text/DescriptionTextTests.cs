using System.Globalization;
using BudgetTracker.Api.Text;

namespace BudgetTracker.Api.Tests.Text;

public class DescriptionTextTests
{
    [Theory]
    [InlineData("Coffee", "coffee")]
    [InlineData("  Coffee  ", "coffee")]
    [InlineData("STARBUCKS #12", "starbucks #12")]
    [InlineData("", "")]
    public void Normalizing_trims_and_ignores_case(string input, string expected)
    {
        Assert.Equal(expected, DescriptionText.Normalize(input));
    }

    [Fact]
    public void Normalizing_leaves_inner_whitespace_alone_because_it_feeds_import_hashes()
    {
        // Import hashes are a contract with stored data; this function must not start collapsing
        // spaces. Matching collapses them in NormalizeForMatching instead.
        Assert.Equal("a   b", DescriptionText.Normalize("A   B"));
    }

    [Fact]
    public void Composed_and_decomposed_accents_are_the_same()
    {
        Assert.Equal(DescriptionText.Normalize("café"), DescriptionText.Normalize("café"));
    }

    [Fact]
    public void Lowercasing_ignores_the_machines_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            // Turkish would lowercase an ASCII "I" to a dotless "ı" if the culture were used.
            Assert.Equal("istanbul", DescriptionText.Normalize("ISTANBUL"));
            Assert.Equal("istanbul", DescriptionText.NormalizeForMatching("ISTANBUL"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("a  b", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("a \n\t b", "a b")]
    [InlineData("  CARD   PURCHASE  COFFEE ", "card purchase coffee")]
    public void Matching_collapses_runs_of_whitespace(string input, string expected)
    {
        Assert.Equal(expected, DescriptionText.NormalizeForMatching(input));
    }

    [Theory]
    [InlineData("Coffee Shop")]
    [InlineData("  ODD   Spacing  and CASE ")]
    [InlineData("café")]
    public void Both_normalizations_are_idempotent(string input)
    {
        Assert.Equal(DescriptionText.Normalize(input), DescriptionText.Normalize(DescriptionText.Normalize(input)));
        Assert.Equal(
            DescriptionText.NormalizeForMatching(input),
            DescriptionText.NormalizeForMatching(DescriptionText.NormalizeForMatching(input)));
    }
}
