using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

/// <summary>The public demo: the login page offers the shared account, and there is no sign-up.</summary>
[Collection(AppCollection.Name)]
public class DemoModeTests(AppFixture app)
{
    private const string DemoPassword = "demo password for browser tests 42";

    private async Task<(WebApplicationFactory<Program> Factory, string Url)> StartDemoAsync()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = app.ConnectionString,
                ["RateLimiting:Auth:PermitLimit"] = "10000",
                ["Demo:ShowCredentials"] = "true",
                ["Demo:Password"] = DemoPassword,
            }));
        });
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, AppFixture.FreePort()));
        factory.StartServer();
        var url = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

        // Seed the account the way the deployment does, with the same command.
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await BudgetTracker.Api.Maintenance.MaintenanceCommands.RunAsync(["seed-demo"], factory.Services));
        return (factory, url);
    }

    [Fact]
    public async Task The_login_page_is_filled_in_explained_and_one_click_signs_in()
    {
        var (factory, url) = await StartDemoAsync();
        await using var _ = factory;
        var context = await app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = url });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(10_000);

        await page.GotoAsync("/");

        await Assertions.Expect(page.GetByLabel("Email")).ToHaveValueAsync("demo@example.com");
        await Assertions.Expect(page.GetByLabel("Password")).ToHaveValueAsync(DemoPassword);
        await Assertions.Expect(page.GetByRole(AriaRole.Note)).ToContainTextAsync("shares one account");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create an account" })).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "Log in" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation)).ToContainTextAsync("Transactions");
    }

    [Fact]
    public async Task The_ordinary_deployment_has_no_prefill_and_keeps_sign_up()
    {
        var page = await app.NewPageAsync();

        await page.GotoAsync("/");

        await Assertions.Expect(page.GetByLabel("Email")).ToHaveValueAsync("");
        await Assertions.Expect(page.GetByLabel("Password")).ToHaveValueAsync("");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create an account" })).ToBeVisibleAsync();
        var response = await page.APIRequest.GetAsync("/demo");
        Assert.Equal(404, response.Status);
    }
}
