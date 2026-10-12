using BudgetTracker.Api.Paging;

namespace BudgetTracker.Api.Tests.Paging;

public class PageParserTests
{
    private static string[] Codes(IEnumerable<BudgetTracker.Api.Auth.ValidationError> errors) =>
        [.. errors.Select(e => e.Code)];

    [Fact]
    public void With_nothing_specified_the_page_starts_at_the_top_with_the_default_limit()
    {
        var (page, errors) = PageParser.Parse(limit: null, cursor: null);

        Assert.Empty(errors);
        Assert.Equal(new PageRequest(50, After: null), page);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(25, 25)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(1_000_000, 100)]
    [InlineData(int.MaxValue, 100)]
    public void The_limit_is_clamped_to_the_maximum(int requested, int expected)
    {
        var (page, errors) = PageParser.Parse(requested, cursor: null);

        Assert.Empty(errors);
        Assert.Equal(expected, page!.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_limit_below_one_is_rejected(int requested)
    {
        var (page, errors) = PageParser.Parse(requested, cursor: null);

        Assert.Null(page);
        Assert.Equal(["limit.invalid"], Codes(errors));
    }

    [Fact]
    public void A_valid_cursor_is_decoded_into_the_request()
    {
        var cursor = new TransactionCursor(new DateOnly(2026, 10, 1), Guid.CreateVersion7());

        var (page, errors) = PageParser.Parse(limit: 10, cursor.Encode());

        Assert.Empty(errors);
        Assert.Equal(new PageRequest(10, cursor), page);
    }

    [Fact]
    public void An_invalid_cursor_is_rejected()
    {
        var (page, errors) = PageParser.Parse(limit: null, cursor: "garbage");

        Assert.Null(page);
        Assert.Equal(["cursor.invalid"], Codes(errors));
    }

    [Fact]
    public void Both_problems_are_reported_together()
    {
        var (_, errors) = PageParser.Parse(limit: 0, cursor: "garbage");

        Assert.Equal(["limit.invalid", "cursor.invalid"], Codes(errors));
    }
}
