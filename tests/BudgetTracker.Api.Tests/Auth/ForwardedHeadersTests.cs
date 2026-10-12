using System.Net;
using BudgetTracker.Api.Tests.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BudgetTracker.Api.Tests.Auth;

/// <summary>
/// Behind nginx every request arrives from the proxy's address, so the per-client login limit
/// has to read the client's address from X-Forwarded-For - but only when told to, and only from
/// a trusted proxy, or anyone could pick a fresh "address" per attempt and never be limited.
/// </summary>
public class ForwardedHeadersTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<HttpClient> ClientAsync(bool forwarded, IPAddress? peer)
    {
        var connection = await fixture.CreateDatabaseConnectionStringAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,
                ["RateLimiting:Auth:PermitLimit"] = "2",
                ["ForwardedHeaders:Enabled"] = forwarded ? "true" : "false",
            }));

            // The in-memory test server has no network peer; give each request one.
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new PeerAddress(peer)));
        });
        return factory.CreateClient();
    }

    private sealed class PeerAddress(IPAddress? address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { email = "nobody@example.com", password = "not the password" }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task With_forwarding_on_each_client_behind_the_proxy_gets_its_own_budget()
    {
        var client = await ClientAsync(forwarded: true, IPAddress.Loopback);

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.1"));

        // A different client address is a different bucket.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.2"));
    }

    [Fact]
    public async Task With_forwarding_off_the_header_is_ignored_so_it_cannot_be_used_to_dodge_the_limit()
    {
        var client = await ClientAsync(forwarded: false, IPAddress.Parse("198.51.100.7"));

        await LoginAsync(client, "203.0.113.1");
        await LoginAsync(client, "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.3"));
    }

    [Fact]
    public async Task With_forwarding_on_a_header_from_an_untrusted_peer_is_still_ignored()
    {
        var client = await ClientAsync(forwarded: true, IPAddress.Parse("198.51.100.7"));

        await LoginAsync(client, "203.0.113.1");
        await LoginAsync(client, "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.3"));
    }
}
