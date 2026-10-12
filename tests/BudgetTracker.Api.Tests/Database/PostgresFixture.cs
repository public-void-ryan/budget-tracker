using System.Threading;
using BudgetTracker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BudgetTracker.Api.Tests.Database;

/// <summary>
/// One real PostgreSQL container for the whole test run. Migrations are applied once to a
/// template database; each test then gets its own copy (CREATE DATABASE ... TEMPLATE), so
/// tests are isolated without paying for a migration or a container per test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDb = "budget_template";

    private PostgreSqlContainer _container = null!;

    private int _counter;

    public async Task InitializeAsync()
    {
        // Starting can fail transiently (for example the previous run's cleanup container still
        // holding a port), and one failure here fails every test in the run. A failed container
        // cannot be restarted, so each attempt builds a fresh one.
        await Retry.RunAsync(
            async _ =>
            {
                _container = new PostgreSqlBuilder("postgres:17").Build();
                try
                {
                    await _container.StartAsync();
                }
                catch
                {
                    await _container.DisposeAsync();
                    throw;
                }
            },
            attempts: 4,
            delayAfter: attempt => TimeSpan.FromSeconds(3 * attempt));

        await ExecuteAdminAsync($"CREATE DATABASE {TemplateDb}");

        await using var context = CreateContext(ConnectionStringFor(TemplateDb));
        await context.Database.MigrateAsync();

        // A template database cannot be copied while any connection to it is open.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _counter)}";
        await ExecuteAdminAsync($"CREATE DATABASE {name} TEMPLATE {TemplateDb}");
        return ConnectionStringFor(name);
    }

    public async Task<BudgetDbContext> CreateDatabaseAsync() =>
        CreateContext(await CreateDatabaseConnectionStringAsync());

    public BudgetDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<BudgetDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new BudgetDbContext(options);
    }

    // Pooling is off: every test gets its own database, so pooled connections would never be
    // reused, and idle ones left behind by a few hundred tests exhaust the server's
    // max_connections. Tests run one at a time, so this costs a few milliseconds per query.
    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Pooling = false,
        }.ConnectionString;

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
