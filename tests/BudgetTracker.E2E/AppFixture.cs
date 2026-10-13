using BudgetTracker.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace BudgetTracker.E2E;

/// <summary>
/// The real application on a real Kestrel port, a real PostgreSQL container and a real Chromium.
/// One of each for the whole run; every test signs up its own user, so tests do not share data.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private IPlaywright _playwright = null!;

    public IBrowser Browser { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Starting can fail transiently (a previous run's cleanup container still holding a port).
        // A failed container cannot be restarted, so each attempt builds a fresh one.
        for (var attempt = 1; ; attempt++)
        {
            _container = new PostgreSqlBuilder("postgres:17").Build();
            try
            {
                await _container.StartAsync();
                break;
            }
            catch when (attempt < 4)
            {
                await _container.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }

        var connection = _container.GetConnectionString();
        await using (var context = new BudgetDbContext(
            new DbContextOptionsBuilder<BudgetDbContext>().UseNpgsql(connection).UseSnakeCaseNamingConvention().Options))
        {
            await context.Database.MigrateAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,
                ["Registration:Enabled"] = "true",
                ["Documentation:Enabled"] = "true",
                ["RateLimiting:Auth:PermitLimit"] = "10000",
            }));
        });
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseUrl = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        _playwright.Dispose();
        await _factory.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>A fresh, isolated browser session (no shared storage with other tests).</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        context.SetDefaultTimeout(10_000);
        return await context.NewPageAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}
