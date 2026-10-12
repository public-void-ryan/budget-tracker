using System.Net;
using System.Text.Json;
using BudgetTracker.Api.Tests.Database;

namespace BudgetTracker.Api.Tests.Auth;

public class ApiDocumentationTests(PostgresFixture fixture) : ApiTest(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } =
        new Dictionary<string, string?> { ["Documentation:Enabled"] = "true" };

    private async Task<JsonElement> DocumentAsync()
    {
        var response = await Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static JsonElement Operation(JsonElement document, string path, string method) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static string[] ResponseCodes(JsonElement operation) =>
        [.. operation.GetProperty("responses").EnumerateObject().Select(r => r.Name).Order()];

    [Fact]
    public async Task Every_endpoint_is_in_the_document()
    {
        var document = await DocumentAsync();

        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => $"{op.Name.ToUpperInvariant()} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "DELETE /accounts/{id}",
                "DELETE /categories/{id}",
                "DELETE /me",
                "DELETE /transactions/{id}",
                "GET /accounts",
                "GET /accounts/{id}",
                "GET /categories",
                "GET /categories/{id}",
                "GET /health",
                "GET /me",
                "GET /transactions",
                "GET /transactions/{id}",
                "POST /accounts",
                "POST /accounts/{id}/imports",
                "POST /accounts/{id}/unarchive",
                "POST /auth/login",
                "POST /auth/logout",
                "POST /auth/register",
                "POST /categories",
                "POST /transactions",
                "PUT /accounts/{id}",
                "PUT /categories/{id}",
                "PUT /transactions/{id}",
            ],
            operations);
    }

    [Fact]
    public async Task Every_operation_has_a_summary()
    {
        var document = await DocumentAsync();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                Assert.True(
                    operation.Value.TryGetProperty("summary", out var summary) && summary.GetString()!.Length > 0,
                    $"{operation.Name} {path.Name} has no summary");
            }
        }
    }

    [Fact]
    public async Task The_bearer_scheme_is_declared()
    {
        var document = await DocumentAsync();

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
    }

    [Theory]
    [InlineData("/auth/logout", "post")]
    [InlineData("/me", "get")]
    [InlineData("/me", "delete")]
    [InlineData("/transactions", "get")]
    [InlineData("/transactions", "post")]
    [InlineData("/transactions/{id}", "get")]
    [InlineData("/transactions/{id}", "put")]
    [InlineData("/transactions/{id}", "delete")]
    [InlineData("/accounts", "get")]
    [InlineData("/accounts", "post")]
    [InlineData("/accounts/{id}", "get")]
    [InlineData("/accounts/{id}", "put")]
    [InlineData("/accounts/{id}", "delete")]
    [InlineData("/accounts/{id}/imports", "post")]
    [InlineData("/accounts/{id}/unarchive", "post")]
    [InlineData("/categories", "get")]
    [InlineData("/categories", "post")]
    [InlineData("/categories/{id}", "get")]
    [InlineData("/categories/{id}", "put")]
    [InlineData("/categories/{id}", "delete")]
    public async Task Endpoints_that_need_a_session_require_the_bearer_scheme(string path, string method)
    {
        var operation = Operation(await DocumentAsync(), path, method);

        var requirements = operation.GetProperty("security").EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject().Select(p => p.Name))
            .ToArray();
        Assert.Contains("Bearer", requirements);
    }

    [Theory]
    [InlineData("/auth/register", "post")]
    [InlineData("/auth/login", "post")]
    [InlineData("/health", "get")]
    public async Task Public_endpoints_do_not_require_the_bearer_scheme(string path, string method)
    {
        var operation = Operation(await DocumentAsync(), path, method);

        Assert.False(operation.TryGetProperty("security", out _));
    }

    [Fact]
    public async Task Login_documents_its_success_and_failure_responses()
    {
        var document = await DocumentAsync();

        Assert.Equal(["200", "400", "401", "429"], ResponseCodes(Operation(document, "/auth/login", "post")));
    }

    [Fact]
    public async Task Registration_documents_every_status_it_can_return()
    {
        var document = await DocumentAsync();

        Assert.Equal(
            ["201", "400", "404", "409", "429"],
            ResponseCodes(Operation(document, "/auth/register", "post")));
    }

    [Fact]
    public async Task Account_deletion_documents_the_password_failure_and_the_rate_limit()
    {
        var document = await DocumentAsync();

        Assert.Equal(
            ["204", "400", "401", "403", "429"],
            ResponseCodes(Operation(document, "/me", "delete")));
    }

    [Fact]
    public async Task The_documentation_page_is_served()
    {
        var response = await Client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}

public class ApiDocumentationDisabledTests(PostgresFixture fixture) : ApiTest(fixture)
{
    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task The_documentation_is_off_unless_switched_on(string url)
    {
        // The public demo must not expose its API explorer by default.
        var response = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
