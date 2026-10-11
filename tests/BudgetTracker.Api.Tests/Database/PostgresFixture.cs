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

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17").Build();

    private int _counter;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ExecuteAdminAsync($"CREATE DATABASE {TemplateDb}");

        await using var context = CreateContext(TemplateDb);
        await context.Database.MigrateAsync();

        // A template database cannot be copied while any connection to it is open.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<BudgetDbContext> CreateDatabaseAsync()
    {
        var name = $"test_{Interlocked.Increment(ref _counter)}";
        await ExecuteAdminAsync($"CREATE DATABASE {name} TEMPLATE {TemplateDb}");
        return CreateContext(name);
    }

    private BudgetDbContext CreateContext(string database)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<BudgetDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new BudgetDbContext(options);
    }

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
