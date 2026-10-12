using System.Net.Http.Json;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Maintenance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BudgetTracker.Api.Tests.Database;

public class MaintenanceCommandTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private async Task<(WebApplicationFactory<Program> Factory, string Connection)> FactoryAsync(
        string? connection = null, string? demoPassword = null)
    {
        connection ??= await fixture.CreateDatabaseConnectionStringAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connection,
                ["Demo:Password"] = demoPassword,
            }));
        });
        return (factory, connection);
    }

    private static async Task<int> RunAsync(WebApplicationFactory<Program> factory, params string[] args)
    {
        _ = factory.Server;   // builds the host without serving anything
        return await MaintenanceCommands.RunAsync(args, factory.Services);
    }

    [Theory]
    [InlineData("migrate", true)]
    [InlineData("seed-demo", true)]
    [InlineData("--urls", false)]
    [InlineData("--environment=Production", false)]
    public void Only_a_bare_first_word_is_a_command_not_a_configuration_switch(string first, bool command) =>
        Assert.Equal(command, MaintenanceCommands.IsCommand([first]));

    [Fact]
    public void No_arguments_means_serve()
    {
        Assert.False(MaintenanceCommands.IsCommand([]));
    }

    [Fact]
    public async Task Migrate_creates_the_schema_in_an_empty_database_and_is_safe_to_repeat()
    {
        var admin = await fixture.CreateDatabaseConnectionStringAsync();
        var builder = new NpgsqlConnectionStringBuilder(admin);
        var empty = builder.Database + "_empty";
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(admin) { Database = "postgres" }.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {empty}", connection);
            await command.ExecuteNonQueryAsync();
        }

        builder.Database = empty;
        var (factory, connectionString) = await FactoryAsync(builder.ConnectionString);

        Assert.Equal(0, await RunAsync(factory, "migrate"));
        Assert.Equal(0, await RunAsync(factory, "migrate"));   // second run: nothing pending

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BudgetTracker.Api.Data.BudgetDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotNull(connectionString);
    }

    [Fact]
    public async Task An_unknown_command_fails_without_touching_anything()
    {
        var (factory, _) = await FactoryAsync();

        Assert.Equal(2, await RunAsync(factory, "drop-everything"));
    }

    [Fact]
    public async Task Seeding_without_a_password_is_refused()
    {
        var (factory, _) = await FactoryAsync();

        Assert.Equal(2, await RunAsync(factory, "seed-demo"));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<BudgetTracker.Api.Data.BudgetDbContext>().Users.AnyAsync());
    }

    [Fact]
    public async Task Seeding_creates_a_demo_user_who_can_log_in_and_whose_data_adds_up()
    {
        var (factory, _) = await FactoryAsync(demoPassword: "demo password for tests 42");
        Assert.Equal(0, await RunAsync(factory, "seed-demo"));

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/login", new { email = DemoData.Email, password = "demo password for tests 42" });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var summary = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/budgets/summary?month={month}");
        Assert.True(summary.GetProperty("categories").GetArrayLength() >= 4);
        Assert.True(summary.GetProperty("totals").GetProperty("budgeted").GetDecimal() > 0);

        // The rules leave this month's groceries and dining for a visitor to apply.
        var dry = await (await client.PostAsync("/rules/apply?dryRun=true", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(dry.GetProperty("categorized").GetInt32() > 0 || DateTime.UtcNow.Day < 2);
    }

    [Fact]
    public async Task Seeding_again_replaces_the_demo_data_and_leaves_other_users_alone()
    {
        var (factory, _) = await FactoryAsync(demoPassword: "demo password for tests 42");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BudgetTracker.Api.Data.BudgetDbContext>();
            db.Users.Add(new BudgetTracker.Api.Data.Entities.User { Email = "real@example.com", PasswordHash = "x" });
            await db.SaveChangesAsync();
        }

        await RunAsync(factory, "seed-demo");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // A visitor makes a mess.
            var db = scope.ServiceProvider.GetRequiredService<BudgetTracker.Api.Data.BudgetDbContext>();
            await db.Transactions.ExecuteDeleteAsync();
        }

        await RunAsync(factory, "seed-demo");

        await using var check = factory.Services.CreateAsyncScope();
        var finalDb = check.ServiceProvider.GetRequiredService<BudgetTracker.Api.Data.BudgetDbContext>();
        Assert.Equal(1, await finalDb.Users.CountAsync(u => u.Email == DemoData.Email));
        Assert.Equal(1, await finalDb.Users.CountAsync(u => u.Email == "real@example.com"));
        Assert.True(await finalDb.Transactions.CountAsync() > 30);
    }
}
