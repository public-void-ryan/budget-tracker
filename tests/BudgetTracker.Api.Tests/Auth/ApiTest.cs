using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Tests.Database;

namespace BudgetTracker.Api.Tests.Auth;

/// <summary>
/// Drives the real application over HTTP against a real PostgreSQL database that belongs to
/// this one test. <see cref="Db"/> reads and writes the same database directly.
/// </summary>
[Collection(PostgresCollection.Name)]
public abstract class ApiTest(PostgresFixture fixture) : IAsyncLifetime
{
    protected const string GoodPassword = "correct horse battery staple";

    private ApiFactory _factory = null!;

    protected HttpClient Client { get; private set; } = null!;
    protected BudgetDbContext Db { get; private set; } = null!;

    /// <summary>Per-test overrides of application configuration.</summary>
    protected virtual IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>();

    public async Task InitializeAsync()
    {
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();
        Db = fixture.CreateContext(connectionString);
        _factory = new ApiFactory(connectionString, Settings);
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    protected Task<HttpResponseMessage> PostAsync(string url, object body) => Client.PostAsJsonAsync(url, body);

    protected async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Registers and logs in through the real endpoints; returns the bearer token.</summary>
    protected async Task<string> RegisterAndLoginAsync(string email, string password = GoodPassword)
    {
        var registered = await PostAsync("/auth/register", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.Created, registered.StatusCode);

        var login = await PostAsync("/auth/login", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        return (await ReadJsonAsync(login)).GetProperty("token").GetString()!;
    }

    /// <summary>
    /// Inserts a user and a live session straight into the database and returns the bearer
    /// token, skipping the (deliberately slow) password hashing. The real authentication
    /// handler and session lookup are still what the request goes through.
    /// </summary>
    protected async Task<(User User, string Token)> CreateUserWithSessionAsync(
        string? email = null, TimeSpan? lifetime = null, TimeSpan? age = null)
    {
        var user = new User
        {
            Email = email ?? $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "not-a-real-hash",
        };
        Db.Add(user);

        var (token, hash) = SessionTokens.Generate();
        var created = DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero);
        Db.Add(new Session
        {
            UserId = user.Id,
            TokenHash = hash,
            CreatedAt = created,
            ExpiresAt = created + (lifetime ?? TimeSpan.FromDays(14)),
        });
        await Db.SaveChangesAsync();
        return (user, token);
    }

    protected HttpRequestMessage Get(string url, string? token = null, string scheme = "Bearer")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        }

        return request;
    }

    protected static byte[] HashOf(string token) => SHA256.HashData(System.Buffers.Text.Base64Url.DecodeFromChars(token));
}
