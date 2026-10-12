using Microsoft.Playwright;

namespace BudgetTracker.E2E;

[Collection(AppCollection.Name)]
public class AuthFlowTests(AppFixture app)
{
    private const string Password = "correct horse battery staple 42";

    private static string NewEmail() => $"e2e-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task A_new_user_can_register_see_the_app_and_log_out()
    {
        var page = await app.NewPageAsync();
        var email = NewEmail();

        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create an account" }).ClickAsync();
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(email).First).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Log out" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_wrong_password_is_explained_and_does_not_sign_in()
    {
        var page = await app.NewPageAsync();
        var email = NewEmail();
        await RegisterAsync(page, email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log out" }).ClickAsync();

        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync("not the password at all");
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Email or password is incorrect");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview" })).ToBeHiddenAsync();
    }

    [Fact]
    public async Task A_weak_password_is_refused_with_the_servers_reason()
    {
        var page = await app.NewPageAsync();

        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create an account" }).ClickAsync();
        await page.GetByLabel("Email").FillAsync(NewEmail());
        await page.GetByLabel("Password").FillAsync("short");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).Not.ToBeEmptyAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview" })).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Reloading_keeps_the_session_but_a_new_tab_context_does_not()
    {
        var page = await app.NewPageAsync();
        await RegisterAsync(page, NewEmail());

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview" })).ToBeVisibleAsync();

        var other = await app.NewPageAsync();
        await other.GotoAsync("/");
        await Assertions.Expect(other.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_revoked_session_sends_the_user_back_to_login()
    {
        var page = await app.NewPageAsync();
        await RegisterAsync(page, NewEmail());

        // Another device logs the user out everywhere: simulate by replacing the stored token.
        await page.EvaluateAsync("sessionStorage.setItem('bt.token', 'not-a-real-token')");
        await page.ReloadAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_page_is_served_with_a_content_security_policy_and_runs_no_inline_script()
    {
        var page = await app.NewPageAsync();
        var violations = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(message.Text);
            }
        };

        var response = await page.GotoAsync("/");
        await RegisterAsync(page, NewEmail());

        Assert.Contains("script-src 'self'", response!.Headers["content-security-policy"]);
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Logging_out_removes_the_token_from_the_browser_and_revokes_it_on_the_server()
    {
        var page = await app.NewPageAsync();
        await RegisterAsync(page, NewEmail());
        var token = await page.EvaluateAsync<string>("sessionStorage.getItem('bt.token')");
        Assert.False(string.IsNullOrEmpty(token));

        await page.GetByRole(AriaRole.Button, new() { Name = "Log out" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Log in" })).ToBeVisibleAsync();

        Assert.Null(await page.EvaluateAsync<string?>("sessionStorage.getItem('bt.token')"));
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.GetAsync("/me")).StatusCode);
    }

    internal static async Task RegisterAsync(IPage page, string email)
    {
        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create an account" }).ClickAsync();
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview" })).ToBeVisibleAsync();
    }
}
