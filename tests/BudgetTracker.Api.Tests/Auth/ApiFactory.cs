using BudgetTracker.Api.Tests.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BudgetTracker.Api.Tests.Auth;

/// <summary>The real application, pointed at one test's own database.</summary>
public sealed class ApiFactory(
    string connectionString, IReadOnlyDictionary<string, string?> settings, TimeProvider time)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["Registration:Enabled"] = "true",

            // High enough to never trip by accident; the rate-limit tests lower it.
            ["RateLimiting:Auth:PermitLimit"] = "1000",
        };

        // Overrides replace defaults: the in-memory provider rejects duplicate keys.
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));

        // Tests control time: session expiry, the purge interval and the cap all read this clock.
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(time);
        });
    }
}
