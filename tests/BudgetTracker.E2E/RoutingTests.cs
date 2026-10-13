using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class RoutingTests(AppFixture app)
{
    private const string Password = "correct horse battery staple 42";

    private async Task<(IPage Page, Api Api, string Email)> SignedInAsync()
    {
        var page = await app.NewPageAsync();
        var email = $"e2e-{Guid.NewGuid():N}@example.com";
        await AuthFlowTests.RegisterAsync(page, email);
        return (page, await Api.SignedInAsync(app, page), email);
    }

    private static ILocator Heading(IPage page, string name) =>
        page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true });

    [Fact]
    public async Task Screens_have_clean_addresses_and_no_hash()
    {
        var (page, _, _) = await SignedInAsync();

        foreach (var (link, path, heading) in new[]
        {
            ("Transactions", "/ui/transactions", "Transactions"),
            ("Import", "/ui/import", "Import"),
            ("Budgets", "/ui/budgets", "Budgets"),
            ("Rules", "/ui/rules", "Rules"),
            ("Accounts and categories", "/ui/setup", "Accounts and categories"),
            ("Overview", "/", "Overview"),
        })
        {
            await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = link, Exact = true }).ClickAsync();
            await Assertions.Expect(Heading(page, heading)).ToBeVisibleAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^http://[^/]+{Regex.Escape(path)}$"));
            Assert.DoesNotContain('#', page.Url);
        }
    }

    [Fact]
    public async Task Navigating_in_the_app_does_not_reload_the_page()
    {
        var (page, _, _) = await SignedInAsync();
        await page.EvaluateAsync("window.__marker = 'still here'");

        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Rules", Exact = true }).ClickAsync();
        await Assertions.Expect(Heading(page, "Rules")).ToBeVisibleAsync();

        Assert.Equal("still here", await page.EvaluateAsync<string>("window.__marker"));
    }

    [Fact]
    public async Task Reloading_a_screen_stays_on_it()
    {
        var (page, _, _) = await SignedInAsync();
        await page.GotoAsync("/ui/budgets");
        await Assertions.Expect(Heading(page, "Budgets")).ToBeVisibleAsync();

        await page.ReloadAsync();

        await Assertions.Expect(Heading(page, "Budgets")).ToBeVisibleAsync();
        Assert.EndsWith("/ui/budgets", page.Url);
    }

    [Fact]
    public async Task Back_and_forward_move_between_screens()
    {
        var (page, _, _) = await SignedInAsync();
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Rules", Exact = true }).ClickAsync();
        await Assertions.Expect(Heading(page, "Rules")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }).ClickAsync();
        await Assertions.Expect(Heading(page, "Import")).ToBeVisibleAsync();

        await page.GoBackAsync();
        await Assertions.Expect(Heading(page, "Rules")).ToBeVisibleAsync();
        await page.GoBackAsync();
        await Assertions.Expect(Heading(page, "Overview")).ToBeVisibleAsync();
        await page.GoForwardAsync();
        await Assertions.Expect(Heading(page, "Rules")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_current_screen_is_marked_in_the_navigation()
    {
        var (page, _, _) = await SignedInAsync();

        await page.GotoAsync("/ui/import");

        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true })).ToHaveAttributeAsync("aria-current", "page");
    }

    [Fact]
    public async Task A_deep_link_while_signed_out_asks_for_login_then_lands_on_that_screen()
    {
        var page = await app.NewPageAsync();
        var email = $"e2e-{Guid.NewGuid():N}@example.com";
        using (var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) })
        {
            (await http.PostAsJsonAsync("/auth/register", new { email, password = Password })).EnsureSuccessStatusCode();
        }

        await page.GotoAsync("/ui/rules");
        await Assertions.Expect(Heading(page, "Log in")).ToBeVisibleAsync();
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in" }).ClickAsync();

        await Assertions.Expect(Heading(page, "Rules")).ToBeVisibleAsync();
        Assert.EndsWith("/ui/rules", page.Url);
    }

    [Fact]
    public async Task Logging_out_returns_to_the_root_address()
    {
        var (page, _, _) = await SignedInAsync();
        await page.GotoAsync("/ui/transactions");
        await Assertions.Expect(Heading(page, "Transactions")).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Log out" }).ClickAsync();

        await Assertions.Expect(Heading(page, "Log in")).ToBeVisibleAsync();
        Assert.Matches(new Regex(@"^http://[^/]+/$"), page.Url);
    }

    [Theory]
    [InlineData("#/transactions", "/ui/transactions", "Transactions")]
    [InlineData("#/budgets", "/ui/budgets", "Budgets")]
    [InlineData("#/", "/", "Overview")]
    public async Task Old_hash_links_are_upgraded_to_clean_addresses(string hash, string path, string heading)
    {
        var (page, _, _) = await SignedInAsync();

        await page.GotoAsync("/" + hash);

        await Assertions.Expect(Heading(page, heading)).ToBeVisibleAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new Regex($"^http://[^/]+{Regex.Escape(path)}$"));
    }

    [Theory]
    [InlineData("/ui/nope")]
    [InlineData("/ui/transactions/extra/segments")]
    public async Task An_unknown_address_says_so_and_links_home(string path)
    {
        var (page, _, _) = await SignedInAsync();

        await page.GotoAsync(path);

        await Assertions.Expect(Heading(page, "Page not found")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to the overview" }).ClickAsync();
        await Assertions.Expect(Heading(page, "Overview")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_trailing_slash_and_the_bare_ui_address_both_work()
    {
        var (page, _, _) = await SignedInAsync();

        await page.GotoAsync("/ui/budgets/");
        await Assertions.Expect(Heading(page, "Budgets")).ToBeVisibleAsync();
        await page.GotoAsync("/ui");
        await Assertions.Expect(Heading(page, "Overview")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task When_the_API_explorer_is_on_the_navigation_links_to_it_and_it_serves_the_spec()
    {
        var (page, _, _) = await SignedInAsync();

        var link = page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "API docs", Exact = true });
        await Assertions.Expect(link).ToHaveAttributeAsync("href", "/scalar/v1");
        await Assertions.Expect(link).ToHaveAttributeAsync("target", "_blank");

        var docs = await page.APIRequest.GetAsync("/scalar/v1");
        Assert.True(docs.Ok);
        var spec = await page.APIRequest.GetAsync("/openapi/v1.json");
        Assert.True(spec.Ok);
        Assert.Contains("\"/budgets/summary\"", await spec.TextAsync());
    }

    [Fact]
    public async Task The_import_pages_link_to_transactions_navigates_in_place()
    {
        var (page, api, _) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await page.GotoAsync("/ui/import");
        await page.GetByLabel("CSV file").SetInputFilesAsync(new FilePayload
        {
            Name = "s.csv", MimeType = "text/csv", Buffer = System.Text.Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-10-01,x,-1.00\n"),
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Import 1 new transaction" }).ClickAsync();

        await page.GetByRole(AriaRole.Link, new() { Name = "View transactions" }).ClickAsync();

        await Assertions.Expect(Heading(page, "Transactions")).ToBeVisibleAsync();
        Assert.EndsWith("/ui/transactions", page.Url);
    }
}
