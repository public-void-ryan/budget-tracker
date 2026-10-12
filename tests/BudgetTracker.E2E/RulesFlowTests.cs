using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class RulesFlowTests(AppFixture app)
{
    private async Task<(IPage Page, Api Api)> SignedInAsync()
    {
        var page = await app.NewPageAsync();
        await AuthFlowTests.RegisterAsync(page, $"e2e-{Guid.NewGuid():N}@example.com");
        return (page, await Api.SignedInAsync(app, page));
    }

    private static async Task OpenRulesAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Rules", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Rules", Exact = true })).ToBeVisibleAsync();
    }

    private static ILocator RuleList(IPage page) => page.GetByRole(AriaRole.List, new() { Name = "Rules in the order they are tried" });

    private static async Task AddRuleAsync(IPage page, string text, string? category = null, string matchType = "contains", bool transfer = false)
    {
        await page.GetByLabel("When the description").SelectOptionAsync(new SelectOptionValue { Label = matchType });
        await page.GetByLabel("Text").FillAsync(text);
        if (category is not null)
        {
            await page.GetByLabel("Set category").SelectOptionAsync(new SelectOptionValue { Label = category });
        }

        if (transfer)
        {
            await page.GetByLabel("Mark as transfer").CheckAsync();
        }

        await page.GetByRole(AriaRole.Button, new() { Name = "Add rule" }).ClickAsync();
    }

    [Fact]
    public async Task Rules_can_be_added_reordered_and_deleted()
    {
        var (page, api) = await SignedInAsync();
        await api.CategoryAsync("Groceries");
        await api.CategoryAsync("Dining");
        await OpenRulesAsync(page);

        await AddRuleAsync(page, "kroger", "Groceries");
        await Assertions.Expect(RuleList(page)).ToContainTextAsync("kroger");
        await AddRuleAsync(page, "starbucks", "Dining", "starts with");
        var items = RuleList(page).GetByRole(AriaRole.Listitem);
        await Assertions.Expect(items).ToHaveCountAsync(2);
        await Assertions.Expect(items.Nth(0)).ToContainTextAsync("kroger");
        await Assertions.Expect(items.Nth(1)).ToContainTextAsync("starts with");

        await page.GetByRole(AriaRole.Button, new() { Name = "Move rule starbucks up" }).ClickAsync();
        await Assertions.Expect(items.Nth(0)).ToContainTextAsync("starbucks");
        var order = (await api.GetAsync("/rules")).EnumerateArray().Select(r => r.GetProperty("pattern").GetString()!).ToArray();
        Assert.Equal(["starbucks", "kroger"], order);   // saved on the server, not only redrawn

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Move rule starbucks up" })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete rule starbucks" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm delete" }).ClickAsync();
        await Assertions.Expect(items).ToHaveCountAsync(1);
        await Assertions.Expect(items.First).ToContainTextAsync("kroger");
    }

    [Fact]
    public async Task A_rule_with_nothing_to_do_is_refused_with_the_servers_reason()
    {
        var (page, _) = await SignedInAsync();
        await OpenRulesAsync(page);

        await AddRuleAsync(page, "nothing");

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).Not.ToBeEmptyAsync();
        await Assertions.Expect(RuleList(page)).ToContainTextAsync("No rules yet.");
    }

    [Fact]
    public async Task Trying_a_description_names_the_first_matching_rule()
    {
        var (page, api) = await SignedInAsync();
        var groceries = await api.CategoryAsync("Groceries");
        var dining = await api.CategoryAsync("Dining");
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "market", matchType = "Contains", categoryId = groceries })).EnsureSuccessStatusCode();
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "super market", matchType = "Contains", categoryId = dining })).EnsureSuccessStatusCode();
        await OpenRulesAsync(page);

        await page.GetByLabel("Try a description").FillAsync("  SUPER   Market  #4 ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Test" }).ClickAsync();
        await Assertions.Expect(page.GetByText("The first matching rule:")).ToContainTextAsync("Groceries");

        await page.GetByLabel("Try a description").FillAsync("hardware");
        await page.GetByRole(AriaRole.Button, new() { Name = "Test" }).ClickAsync();
        await Assertions.Expect(page.GetByText("No rule matches.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Rules_are_previewed_then_applied_to_existing_transactions_and_never_override_a_choice()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var groceries = await api.CategoryAsync("Groceries");
        var dining = await api.CategoryAsync("Dining");
        await api.TransactionAsync(account, "2026-10-01", -10m, "KROGER 1");
        await api.TransactionAsync(account, "2026-10-02", -20m, "KROGER 2");
        await api.TransactionAsync(account, "2026-10-03", -30m, "KROGER chosen by hand", dining);
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "kroger", matchType = "Contains", categoryId = groceries })).EnsureSuccessStatusCode();
        await OpenRulesAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Preview changes" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Would change")).ToContainTextAsync("2 categorized");
        Assert.Equal(2, CountUncategorized(await api.GetAsync("/transactions")));   // a preview changes nothing

        await page.GetByRole(AriaRole.Button, new() { Name = "Apply rules" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Changed")).ToContainTextAsync("2 categorized");
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Matches per rule" })).ToContainTextAsync("kroger");

        var items = (await api.GetAsync("/transactions")).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(0, items.Count(t => t.GetProperty("categoryId").ValueKind == System.Text.Json.JsonValueKind.Null));
        Assert.Equal(dining, items.Single(t => t.GetProperty("description").GetString() == "KROGER chosen by hand").GetProperty("categoryId").GetGuid());
    }

    private static int CountUncategorized(System.Text.Json.JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Count(t => t.GetProperty("categoryId").ValueKind == System.Text.Json.JsonValueKind.Null);
}
