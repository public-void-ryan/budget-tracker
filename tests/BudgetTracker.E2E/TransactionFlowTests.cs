using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class TransactionFlowTests(AppFixture app)
{
    private async Task<(IPage Page, Api Api)> SignedInAsync()
    {
        var page = await app.NewPageAsync();
        await AuthFlowTests.RegisterAsync(page, $"e2e-{Guid.NewGuid():N}@example.com");
        return (page, await Api.SignedInAsync(app, page));
    }

    private static async Task GoToAsync(IPage page, string linkName, string heading)
    {
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = linkName, Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Accounts_and_categories_can_be_added_and_removed_from_the_UI()
    {
        var (page, _) = await SignedInAsync();
        await GoToAsync(page, "Accounts and categories", "Accounts and categories");

        await page.GetByLabel("New category").FillAsync("Groceries");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add category" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Categories" })).ToContainTextAsync("Groceries");

        await page.GetByLabel("New account").FillAsync("Everyday");
        await page.GetByLabel("Type").SelectOptionAsync("Savings");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add account" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Accounts" })).ToContainTextAsync("Everyday");
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Accounts" })).ToContainTextAsync("Savings");

        // Deleting takes two clicks.
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete category Groceries" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Categories" })).ToContainTextAsync("Groceries");
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm delete" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Categories" })).Not.ToContainTextAsync("Groceries");

        await page.GetByRole(AriaRole.Button, new() { Name = "Archive account Everyday" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm archive" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Accounts" })).ToContainTextAsync("archived");
        await page.GetByRole(AriaRole.Button, new() { Name = "Reopen account Everyday" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.List, new() { Name = "Accounts" })).Not.ToContainTextAsync("archived");
    }

    [Fact]
    public async Task A_duplicate_category_name_shows_the_servers_message()
    {
        var (page, api) = await SignedInAsync();
        await api.CategoryAsync("Rent");
        await GoToAsync(page, "Accounts and categories", "Accounts and categories");

        await page.GetByLabel("New category").FillAsync("Rent");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add category" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).Not.ToBeEmptyAsync();
    }

    [Fact]
    public async Task A_transaction_can_be_added_categorized_and_deleted()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await api.CategoryAsync("Dining");
        await GoToAsync(page, "Transactions", "Transactions");

        await page.GetByText("Add a transaction").ClickAsync();
        await page.GetByLabel("Date").First.FillAsync("2026-10-05");
        await page.GetByLabel("Amount").FillAsync("-12.50");
        await page.GetByLabel("Description").FillAsync("Corner cafe");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add transaction" }).ClickAsync();

        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Corner cafe" });
        await Assertions.Expect(row).ToContainTextAsync("-12.50");
        await Assertions.Expect(row).ToContainTextAsync("2026-10-05");

        await row.GetByLabel("Category for Corner cafe").SelectOptionAsync(new SelectOptionValue { Label = "Dining" });
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Category saved");
        var saved = await api.GetAsync("/transactions");
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, saved.GetProperty("items")[0].GetProperty("categoryId").ValueKind);

        await row.GetByRole(AriaRole.Button, new() { Name = "Delete Corner cafe" }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Confirm delete" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Corner cafe" })).ToHaveCountAsync(0);
        Assert.Empty(( await api.GetAsync("/transactions")).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("12.123456")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("")]
    public async Task An_amount_that_is_not_a_plain_decimal_is_refused_before_anything_is_sent(string amount)
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await GoToAsync(page, "Transactions", "Transactions");

        await page.GetByText("Add a transaction").ClickAsync();
        await page.GetByLabel("Amount").FillAsync(amount);
        await page.GetByLabel("Description").FillAsync("x");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add transaction" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("at most 4 decimals");
        Assert.Empty((await api.GetAsync("/transactions")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_long_list_is_paged_and_filters_narrow_it()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var food = await api.CategoryAsync("Food");
        for (var i = 1; i <= 30; i++)
        {
            await api.TransactionAsync(account, $"2026-09-{i:00}", -1m * i, $"item {i:00}", i % 2 == 0 ? food : null);
        }

        await GoToAsync(page, "Transactions", "Transactions");

        var rows = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "item" });
        await Assertions.Expect(rows).ToHaveCountAsync(25);
        await Assertions.Expect(rows.First).ToContainTextAsync("item 30");   // newest first
        await page.GetByRole(AriaRole.Button, new() { Name = "Load more" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(30);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Load more" })).ToBeHiddenAsync();

        await page.Locator("select[name=filter-category]").SelectOptionAsync(new SelectOptionValue { Label = "Food" });
        await page.GetByLabel("From").FillAsync("2026-09-10");
        await page.GetByLabel("To").FillAsync("2026-09-20");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();

        await Assertions.Expect(rows).ToHaveCountAsync(6);   // evens 10..20
        await Assertions.Expect(rows.First).ToContainTextAsync("item 20");
        await Assertions.Expect(rows.Last).ToContainTextAsync("item 10");
    }

    [Fact]
    public async Task The_uncategorized_filter_shows_only_transactions_with_no_category()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        var other = await api.AccountAsync("Other");
        var food = await api.CategoryAsync("Food");
        await api.TransactionAsync(account, "2026-10-01", -1m, "plain-one");
        await api.TransactionAsync(account, "2026-10-02", -2m, "food-two", food);
        await api.TransactionAsync(other, "2026-10-03", -3m, "plain-three");
        await GoToAsync(page, "Transactions", "Transactions");
        var rows = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "plain-" }).Or(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "food-" }));
        await Assertions.Expect(rows).ToHaveCountAsync(3);

        await page.Locator("select[name=filter-category]").SelectOptionAsync(new SelectOptionValue { Label = "Uncategorized" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();

        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "food-two" })).ToHaveCountAsync(0);

        // It combines with the account filter, and "All categories" brings everything back.
        await page.Locator("select[name=filter-account]").SelectOptionAsync(new SelectOptionValue { Label = "Other" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(1);
        await Assertions.Expect(rows.First).ToContainTextAsync("plain-three");

        await page.Locator("select[name=filter-account]").SelectOptionAsync(new SelectOptionValue { Label = "All accounts" });
        await page.Locator("select[name=filter-category]").SelectOptionAsync(new SelectOptionValue { Label = "All categories" });
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task A_transfer_badge_explains_itself()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        (await api.SendAsync(HttpMethod.Post, "/transactions", new
        {
            accountId = account, postedOn = "2026-10-01", amount = -600m, description = "card payment", isTransfer = true,
        })).EnsureSuccessStatusCode();
        await GoToAsync(page, "Transactions", "Transactions");

        var badge = page.Locator("span.pill", new() { HasText = "transfer" });

        await Assertions.Expect(badge).ToHaveAttributeAsync("title", new Regex("not counted as spending or income"));
        await Assertions.Expect(badge).ToContainTextAsync("not counted as spending or income");   // screen-reader text
    }

    [Fact]
    public async Task An_account_with_flipped_signs_is_labelled_in_plain_words_with_an_explanation()
    {
        var (page, api) = await SignedInAsync();
        (await api.SendAsync(HttpMethod.Post, "/accounts", new { name = "Visa", type = "Credit", invertSign = true })).EnsureSuccessStatusCode();
        await GoToAsync(page, "Accounts and categories", "Accounts and categories");

        var badge = page.Locator("span.pill", new() { HasText = "purchases shown positive" });

        await Assertions.Expect(badge).ToHaveAttributeAsync("title", new Regex("flip the sign"));
        await Assertions.Expect(page.GetByText("inverted sign")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Filtering_to_nothing_says_so()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        await api.TransactionAsync(account, "2026-09-01", -1m, "lonely");
        await GoToAsync(page, "Transactions", "Transactions");

        await page.GetByLabel("From").FillAsync("2030-01-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();

        await Assertions.Expect(page.GetByText("No transactions match.")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Descriptions_are_shown_as_text_never_as_markup()
    {
        var (page, api) = await SignedInAsync();
        var account = await api.AccountAsync("Everyday");
        const string hostile = "<img src=x onerror=\"document.title='pwned'\"><b>bold</b>";
        await api.TransactionAsync(account, "2026-09-01", -1m, hostile);

        await GoToAsync(page, "Transactions", "Transactions");

        await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = hostile, Exact = true })).ToBeVisibleAsync();
        Assert.Equal("Budget Tracker", await page.TitleAsync());
        Assert.Equal(0, await page.Locator("main b, main img").CountAsync());
    }

    [Fact]
    public async Task A_session_that_dies_mid_use_returns_to_login()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await GoToAsync(page, "Transactions", "Transactions");

        await page.EvaluateAsync("sessionStorage.setItem('bt.token', 'revoked')");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();
        Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('bt.token')"));
    }
}
