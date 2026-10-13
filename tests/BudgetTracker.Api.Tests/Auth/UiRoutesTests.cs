using System.Net;
using BudgetTracker.Api.Tests.Database;

namespace BudgetTracker.Api.Tests.Auth;

/// <summary>The UI's screens live under /ui/ so they never collide with the API's own paths.</summary>
public class UiRoutesTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/ui")]
    [InlineData("/ui/transactions")]
    [InlineData("/ui/budgets/")]
    [InlineData("/ui/anything/at/all")]
    public async Task Addresses_for_the_UI_serve_the_page_with_the_strict_policy(string path)
    {
        var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("Budget Tracker", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/transactions")]
    [InlineData("/budgets/summary?month=2026-10")]
    [InlineData("/rules")]
    [InlineData("/me")]
    public async Task The_APIs_own_paths_are_not_shadowed_by_the_UI(string path)
    {
        var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unknown_path_outside_the_UI_is_still_a_404_not_the_page()
    {
        var response = await Client.GetAsync("/no-such-thing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task POST_to_a_UI_address_is_not_served_the_page()
    {
        var response = await Client.PostAsync("/ui/transactions", null);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
