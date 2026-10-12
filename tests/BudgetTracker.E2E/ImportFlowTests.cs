using System.Text;
using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class ImportFlowTests(AppFixture app)
{
    private async Task<(IPage Page, Api Api)> SignedInAsync()
    {
        var page = await app.NewPageAsync();
        await AuthFlowTests.RegisterAsync(page, $"e2e-{Guid.NewGuid():N}@example.com");
        return (page, await Api.SignedInAsync(app, page));
    }

    private static async Task OpenImportAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Import", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Import", Exact = true })).ToBeVisibleAsync();
    }

    private static Task ChooseAsync(IPage page, string csv, string name = "statement.csv") =>
        page.GetByLabel("CSV file").SetInputFilesAsync(new FilePayload { Name = name, MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(csv) });

    private const string Statement =
        "Date,Description,Amount\n2026-10-01,KROGER #55,-60.00\n2026-10-02,STARBUCKS #12,-4.50\n2026-10-03,Paycheck,2500.00\n";

    [Fact]
    public async Task A_statement_is_previewed_then_imported_and_a_reupload_finds_only_duplicates()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await OpenImportAsync(page);

        await ChooseAsync(page, Statement);
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        var table = page.GetByRole(AriaRole.Table, new() { Name = "Preview of the import" });
        await Assertions.Expect(table.GetByRole(AriaRole.Row)).ToHaveCountAsync(4);   // header + 3
        await Assertions.Expect(page.GetByText("3 new", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Empty((await api.GetAsync("/transactions")).GetProperty("items").EnumerateArray());   // a preview writes nothing

        await page.GetByRole(AriaRole.Button, new() { Name = "Import 3 new transactions" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Imported 3 transactions")).ToBeVisibleAsync();
        Assert.Equal(3, (await api.GetAsync("/transactions")).GetProperty("items").GetArrayLength());

        await page.ReloadAsync();
        await ChooseAsync(page, Statement);
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        await Assertions.Expect(page.GetByText("0 new", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Nothing new to import" })).ToBeDisabledAsync();
    }

    [Fact]
    public async Task A_bad_file_lists_every_problem_and_imports_nothing()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await OpenImportAsync(page);

        await ChooseAsync(page, "Date,Description,Amount\n2026-10-01,ok,-1.00\nnot-a-date,bad date,-2.00\n2026-10-03,bad amount,abc\n");
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();

        var problems = page.GetByRole(AriaRole.List, new() { Name = "Problems in the file" });
        await Assertions.Expect(problems.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(2);
        await Assertions.Expect(problems).ToContainTextAsync("Row 2");
        await Assertions.Expect(problems).ToContainTextAsync("Row 3");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Import" })).ToHaveCountAsync(0);
        Assert.Empty((await api.GetAsync("/transactions")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_is_explained()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await OpenImportAsync(page);

        var latin1 = Encoding.Latin1.GetBytes("Date,Description,Amount\n2026-10-01,Café,-3.00\n");
        await page.GetByLabel("CSV file").SetInputFilesAsync(new FilePayload { Name = "latin1.csv", MimeType = "text/csv", Buffer = latin1 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("UTF-8");
    }

    [Fact]
    public async Task Previewing_without_a_file_asks_for_one()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await OpenImportAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a CSV file");
    }

    [Fact]
    public async Task A_credit_cards_flipped_signs_show_stored_and_file_amounts_side_by_side()
    {
        var (page, api) = await SignedInAsync();
        var response = await api.SendAsync(HttpMethod.Post, "/accounts", new { name = "Card", type = "Credit", invertSign = true });
        response.EnsureSuccessStatusCode();
        await OpenImportAsync(page);

        await ChooseAsync(page, "Date,Description,Amount\n2026-10-01,Coffee,4.50\n");
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();

        var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Coffee" });
        await Assertions.Expect(row.GetByRole(AriaRole.Cell, new() { Name = "-4.50", Exact = true })).ToHaveCountAsync(1);   // stored
        await Assertions.Expect(row.GetByRole(AriaRole.Cell, new() { Name = "4.50", Exact = true })).ToHaveCountAsync(1);    // in the file
        await Assertions.Expect(page.GetByText("flips signs")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Other_date_layouts_and_column_names_can_be_chosen()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        await OpenImportAsync(page);

        await ChooseAsync(page, "When;What;How much\n03/04/2026;Lunch;-9.00\n".Replace(';', ','));
        await page.GetByLabel("Date format in the file").SelectOptionAsync("eu");
        await page.GetByText("Column names (only if detection fails)").ClickAsync();
        await page.GetByLabel("Date column").FillAsync("When");
        await page.GetByLabel("Description column").FillAsync("What");
        await page.GetByLabel("Amount column").FillAsync("How much");
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();

        // 03/04/2026 read day-first is the 3rd of April.
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Lunch" })).ToContainTextAsync("2026-04-03");
    }

    [Fact]
    public async Task Rules_categorize_rows_in_the_preview_and_the_import_reports_how_many()
    {
        var (page, api) = await SignedInAsync();
        await api.AccountAsync("Everyday");
        var groceries = await api.CategoryAsync("Groceries");
        (await api.SendAsync(HttpMethod.Post, "/rules", new { pattern = "kroger", matchType = "Contains", categoryId = groceries })).EnsureSuccessStatusCode();
        await OpenImportAsync(page);

        await ChooseAsync(page, Statement);
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "KROGER" })).ToContainTextAsync("Groceries");
        await page.GetByRole(AriaRole.Button, new() { Name = "Import 3 new transactions" }).ClickAsync();

        await Assertions.Expect(page.GetByText("1 categorized by your rules")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Archived_accounts_are_not_offered_and_with_no_accounts_the_page_says_what_to_do()
    {
        var (page, api) = await SignedInAsync();
        await OpenImportAsync(page);
        await Assertions.Expect(page.GetByText("Create an account")).ToBeVisibleAsync();

        var id = await api.AccountAsync("Old");
        (await api.SendAsync(HttpMethod.Delete, $"/accounts/{id}")).EnsureSuccessStatusCode();
        await page.ReloadAsync();
        await OpenImportAsync(page);
        await Assertions.Expect(page.GetByText("Create an account")).ToBeVisibleAsync();
    }
}
