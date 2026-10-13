using System.Net;
using BudgetTracker.Api.Tests.Database;

namespace BudgetTracker.Api.Tests.Auth;

/// <summary>/demo exists only on the public demo, where the password is published on purpose.</summary>
public class DemoEndpointTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task It_is_a_404_by_default_like_any_unknown_path()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/demo")).StatusCode);
    }
}

public class DemoEndpointEnabledTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Demo:ShowCredentials"] = "true",
        ["Demo:Password"] = "the published demo password 42",
    };

    [Fact]
    public async Task When_switched_on_it_returns_the_demo_account()
    {
        var response = await Client.GetAsync("/demo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(("demo@example.com", "the published demo password 42"),
            (body.GetProperty("email").GetString(), body.GetProperty("password").GetString()));
    }
}

public class DemoEndpointWithoutPasswordTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Demo:ShowCredentials"] = "true",
    };

    [Fact]
    public async Task Switched_on_without_a_password_it_stays_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/demo")).StatusCode);
    }
}

public class DemoPasswordWithoutSwitchTests(PostgresFixture fixture) : ApiTest(fixture)
{
    // The password is configured on the migrate/seed units too; having it must not publish it.
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Demo:Password"] = "the published demo password 42",
    };

    [Fact]
    public async Task A_configured_password_alone_does_not_expose_the_endpoint()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/demo")).StatusCode);
    }
}
