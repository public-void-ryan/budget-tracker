using System.Text;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class BudgetFlowTests(AppFixture app)
{
    private async Task<(IPage Page, Api Api)> SignedInAsync()
    {
        var page = await app.NewPageAsync();
        await AuthFlowTests.RegisterAsync(page, $"e2e-{Guid.NewGuid():N}@example.com");
        return (page, await Api.SignedInAsync(app, page));
    }

    private static async Task GoToAsync(IPage page, string link, string heading)
    {
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = link, Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Exact = true })).ToBeVisibleAsync();
    }

    private static async Task ChooseMonthAsync(IPage page, string month)
    {
        await page.GetByLabel("Month").FillAsync(month);

        // The screen marks what it is loading; typing before it settles would race the redraw.
        await Assertions.Expect(page.Locator("[aria-busy=true]")).ToHaveCountAsync(0);
    }

    private static ILocator Summary(IPage page) => page.GetByRole(AriaRole.Table, new() { Name = "Budget against actual" });

    private static async Task SetBudgetAsync(IPage page, string category, string amount)
    {
        await page.GetByLabel($"Budget for {category}").FillAsync(amount);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Save {category} budget" }).ClickAsync();
    }

    [Fact]
    public async Task A_budget_set_on_one_screen_shows_up_against_spending_on_the_overview()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var dining = await api.CategoryAsync("Dining");
        var rent = await api.CategoryAsync("Rent");
        await api.TransactionAsync(account, "2026-10-05", -150m, "dinner", dining);
        await api.TransactionAsync(account, "2026-10-09", -80m, "lunch", dining);
        await api.TransactionAsync(account, "2026-10-01", -1000m, "landlord", rent);
        await api.TransactionAsync(account, "2026-10-20", -25m, "mystery");

        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-10");
        await SetBudgetAsync(page, "Dining", "200");
        await Assertions.Expect(page.GetByText("Saved Dining: 200.00.")).ToBeVisibleAsync();
        await SetBudgetAsync(page, "Rent", "1000");
        await Assertions.Expect(page.GetByText("Saved Rent: 1,000.00.")).ToBeVisibleAsync();

        await GoToAsync(page, "Overview", "Overview");   // the month chosen earlier carries over
        await Assertions.Expect(page.GetByLabel("Month")).ToHaveValueAsync("2026-10");
        var diningRow = Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Dining" });
        await Assertions.Expect(diningRow.GetByRole(AriaRole.Cell, new() { Name = "200.00", Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(diningRow.GetByRole(AriaRole.Cell, new() { Name = "230.00", Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(diningRow.GetByRole(AriaRole.Cell, new() { Name = "-30.00", Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(diningRow).ToContainTextAsync("Over by 30.00");

        var rentRow = Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Rent" });
        await Assertions.Expect(rentRow).Not.ToContainTextAsync("Over by");   // exactly on budget is not over
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Uncategorized" })).ToContainTextAsync("25.00");
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Total" })).ToContainTextAsync("1,255.00");
    }

    [Fact]
    public async Task Income_is_shown_apart_from_spending_and_the_net_accounts_for_both()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var rent = await api.CategoryAsync("Rent");
        var groceries = await api.CategoryAsync("Groceries");
        var salary = await api.CategoryAsync("Salary");
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-10/{rent}", new { amount = 1450 })).EnsureSuccessStatusCode();
        await api.TransactionAsync(account, "2026-10-01", -1450m, "landlord", rent);
        await api.TransactionAsync(account, "2026-10-03", -100m, "kroger", groceries);
        await api.TransactionAsync(account, "2026-10-15", 3200m, "payroll", salary);
        await api.TransactionAsync(account, "2026-10-16", 40m, "gift", null);
        await GoToAsync(page, "Overview", "Overview");

        await ChooseMonthAsync(page, "2026-10");

        var spending = Summary(page);
        await Assertions.Expect(spending).Not.ToContainTextAsync("Salary");   // not "negative spending"
        var total = spending.GetByRole(AriaRole.Row).Filter(new() { HasText = "Total" });
        await Assertions.Expect(total).ToContainTextAsync("1,450.00");   // budget
        await Assertions.Expect(total).ToContainTextAsync("1,550.00");   // all spending
        await Assertions.Expect(total).ToContainTextAsync("-100.00");    // budget minus spending

        var income = page.GetByRole(AriaRole.Table, new() { Name = "Income received" });
        await Assertions.Expect(income.GetByRole(AriaRole.Row).Filter(new() { HasText = "Salary" })).ToContainTextAsync("3,200.00");
        await Assertions.Expect(income.GetByRole(AriaRole.Row).Filter(new() { HasText = "Uncategorized" })).ToContainTextAsync("40.00");
        await Assertions.Expect(income.GetByRole(AriaRole.Row).Filter(new() { HasText = "Total" })).ToContainTextAsync("3,240.00");
        await Assertions.Expect(page.Locator("p.net")).ToContainTextAsync("Net for the month: 1,690.00");   // 3,240 - 1,550
    }

    [Fact]
    public async Task Changing_the_month_changes_the_numbers()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var food = await api.CategoryAsync("Food");
        await api.TransactionAsync(account, "2026-09-30", -1m, "september", food);
        await api.TransactionAsync(account, "2026-10-01", -10m, "october", food);
        await GoToAsync(page, "Overview", "Overview");

        await ChooseMonthAsync(page, "2026-09");
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Food" })).ToContainTextAsync("1.00");
        await ChooseMonthAsync(page, "2026-10");
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Food" })).ToContainTextAsync("10.00");
        await ChooseMonthAsync(page, "2030-01");
        await Assertions.Expect(page.GetByText("Nothing to show for this month.")).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.23456")]
    public async Task A_budget_that_is_not_zero_or_more_with_four_decimals_is_refused(string amount)
    {
        var (page, api) = await SignedInAsync();
        await api.CategoryAsync("Food");
        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-10");

        await SetBudgetAsync(page, "Food", amount);

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("zero or more");
        Assert.Empty((await api.GetAsync("/budgets?month=2026-10")).EnumerateArray());
    }

    [Fact]
    public async Task A_budget_can_be_zero_replaced_and_removed()
    {
        var (page, api) = await SignedInAsync();
        await api.CategoryAsync("Food");
        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-10");

        await SetBudgetAsync(page, "Food", "0");
        await Assertions.Expect(page.GetByText("Saved Food: 0.00.")).ToBeVisibleAsync();
        await SetBudgetAsync(page, "Food", "75.5");
        await Assertions.Expect(page.GetByText("Saved Food: 75.50.")).ToBeVisibleAsync();
        Assert.Single((await api.GetAsync("/budgets?month=2026-10")).EnumerateArray());

        await page.GetByRole(AriaRole.Button, new() { Name = "Remove Food budget" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm remove" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Removed the Food budget.")).ToBeVisibleAsync();
        Assert.Empty((await api.GetAsync("/budgets?month=2026-10")).EnumerateArray());
    }

    [Fact]
    public async Task Last_months_budgets_can_be_copied_without_overwriting_this_months()
    {
        var (page, api) = await SignedInAsync();
        var food = await api.CategoryAsync("Food");
        var fun = await api.CategoryAsync("Fun");
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-09/{food}", new { amount = 300 })).EnsureSuccessStatusCode();
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-09/{fun}", new { amount = 50 })).EnsureSuccessStatusCode();
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-10/{food}", new { amount = 999 })).EnsureSuccessStatusCode();
        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-10");

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy last month’s budgets" }).ClickAsync();

        await Assertions.Expect(page.GetByText("Copied 1 budget from 2026-09; 1 already set here were kept.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByLabel("Budget for Fun")).ToHaveValueAsync("50");
        await Assertions.Expect(page.GetByLabel("Budget for Food")).ToHaveValueAsync("999");
    }

    [Fact]
    public async Task January_copies_from_the_previous_december()
    {
        var (page, api) = await SignedInAsync();
        var food = await api.CategoryAsync("Food");
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2025-12/{food}", new { amount = 120 })).EnsureSuccessStatusCode();
        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-01");

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy last month’s budgets" }).ClickAsync();

        await Assertions.Expect(page.GetByLabel("Budget for Food")).ToHaveValueAsync("120");
    }

    [Fact]
    public async Task A_slow_answer_for_a_month_you_have_left_does_not_overwrite_the_one_you_are_on()
    {
        var (page, api) = await SignedInAsync();
        var food = await api.CategoryAsync("Food");
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-09/{food}", new { amount = 111 })).EnsureSuccessStatusCode();
        (await api.SendAsync(HttpMethod.Put, $"/budgets/2026-10/{food}", new { amount = 222 })).EnsureSuccessStatusCode();
        await page.RouteAsync("**/budgets?month=2026-09", async route =>
        {
            await Task.Delay(1500);
            await route.ContinueAsync();
        });
        await GoToAsync(page, "Budgets", "Budgets");

        await page.GetByLabel("Month").FillAsync("2026-09");   // slow
        await page.GetByLabel("Month").FillAsync("2026-10");   // fast, and the one we stay on
        await Assertions.Expect(page.GetByLabel("Budget for Food")).ToHaveValueAsync("222");

        await Task.Delay(2500);   // long enough for the stale September answer to arrive
        await Assertions.Expect(page.GetByLabel("Budget for Food")).ToHaveValueAsync("222");
    }

    [Fact]
    public async Task The_whole_journey_import_rules_budget_overview()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        var groceries = await api.CategoryAsync("Groceries");
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "kroger", matchType = "Contains", categoryId = groceries })).EnsureSuccessStatusCode();
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "payment thank you", matchType = "Contains", markTransfer = true })).EnsureSuccessStatusCode();

        await GoToAsync(page, "Budgets", "Budgets");
        await ChooseMonthAsync(page, "2026-10");
        await SetBudgetAsync(page, "Groceries", "100");
        await Assertions.Expect(page.GetByText("Saved Groceries")).ToBeVisibleAsync();

        await GoToAsync(page, "Import", "Import");
        const string csv = "Date,Description,Amount\n2026-10-01,KROGER #55,-60.00\n2026-10-03,KROGER #55,-55.00\n2026-10-04,PAYMENT THANK YOU,500.00\n2026-10-05,Mystery shop,-10.00\n";
        await page.GetByLabel("CSV file").SetInputFilesAsync(new FilePayload { Name = "s.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(csv) });
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Import 4 new transactions" }).ClickAsync();
        await Assertions.Expect(page.GetByText("2 categorized by your rules; 1 marked as transfers")).ToBeVisibleAsync();

        await GoToAsync(page, "Overview", "Overview");
        var groceriesRow = Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Groceries" });
        await Assertions.Expect(groceriesRow).ToContainTextAsync("115.00");
        await Assertions.Expect(groceriesRow).ToContainTextAsync("Over by 15.00");
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Uncategorized" })).ToContainTextAsync("10.00");
        await Assertions.Expect(Summary(page).GetByRole(AriaRole.Row).Filter(new() { HasText = "Total" })).ToContainTextAsync("125.00");   // the 500.00 transfer is excluded
    }
}
