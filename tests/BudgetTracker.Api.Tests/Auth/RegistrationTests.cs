using System.Net;
using System.Text;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Auth;

public class RegistrationTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task Registering_creates_the_user_and_stores_only_a_hash_of_the_password()
    {
        var response = await PostAsync("/auth/register", new { email = "ryan@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await Db.Users.AsNoTracking().SingleAsync();
        Assert.Equal("ryan@example.com", user.Email);
        Assert.DoesNotContain(GoodPassword, user.PasswordHash);
        Assert.Equal(PasswordVerification.Valid, new PasswordService().Verify(user.PasswordHash, GoodPassword));
    }

    [Fact]
    public async Task The_response_carries_the_user_and_no_password_material()
    {
        var response = await PostAsync("/auth/register", new { email = "ryan@example.com", password = GoodPassword });

        var body = await ReadJsonAsync(response);
        Assert.Equal(
            ["email", "id"],
            body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("ryan@example.com", body.GetProperty("email").GetString());
    }

    [Fact]
    public async Task The_email_is_trimmed_and_lowercased_before_it_is_stored()
    {
        await PostAsync("/auth/register", new { email = "  Ryan@Example.COM ", password = GoodPassword });

        Assert.Equal("ryan@example.com", (await Db.Users.AsNoTracking().SingleAsync()).Email);
    }

    [Fact]
    public async Task Invalid_input_is_a_400_that_names_every_problem_field_and_code()
    {
        var response = await PostAsync("/auth/register", new { email = "not-an-email", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await ReadJsonAsync(response);
        Assert.Equal("email.invalid", body.GetProperty("codes").GetProperty("email")[0].GetString());
        Assert.Equal("password.too_short", body.GetProperty("codes").GetProperty("password")[0].GetString());
        Assert.NotEmpty(body.GetProperty("errors").GetProperty("email")[0].GetString()!);
        Assert.Equal(0, await Db.Users.CountAsync());
    }

    [Fact]
    public async Task A_common_password_is_rejected()
    {
        var response = await PostAsync("/auth/register", new { email = "ryan@example.com", password = "password1234" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("password.too_common", body.GetProperty("codes").GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task An_empty_json_object_reports_both_fields_as_required()
    {
        var response = await Client.PostAsync(
            "/auth/register", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = (await ReadJsonAsync(response)).GetProperty("codes");
        Assert.Equal("email.required", codes.GetProperty("email")[0].GetString());
        Assert.Equal("password.required", codes.GetProperty("password")[0].GetString());
    }

    [Fact]
    public async Task A_duplicate_email_is_a_409_regardless_of_case()
    {
        await PostAsync("/auth/register", new { email = "ryan@example.com", password = GoodPassword });

        var response = await PostAsync("/auth/register", new { email = "RYAN@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email.taken", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, await Db.Users.CountAsync());
    }

    [Fact]
    public async Task Two_simultaneous_registrations_of_one_email_create_exactly_one_user()
    {
        // There is no check-then-insert: the unique index decides, so the loser gets a clean
        // 409 instead of a duplicate row or a 500.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            PostAsync("/auth/register", new { email = "race@example.com", password = GoodPassword })));

        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await Db.Users.CountAsync());
    }
}

public class RegistrationDisabledTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["Registration:Enabled"] = "false" };

    [Fact]
    public async Task Registration_is_a_404_and_creates_nothing_when_switched_off()
    {
        var response = await PostAsync("/auth/register", new { email = "ryan@example.com", password = GoodPassword });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await Db.Users.CountAsync());
    }

    [Fact]
    public async Task Switching_registration_off_does_not_affect_login()
    {
        var (user, _) = await CreateUserWithSessionAsync();
        user.PasswordHash = new PasswordService().Hash(GoodPassword);
        await Db.SaveChangesAsync();

        var response = await PostAsync("/auth/login", new { email = user.Email, password = GoodPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
