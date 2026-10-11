using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

public class MigrationTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task Migrations_apply_to_an_empty_database()
    {
        var applied = await Db.Database.GetAppliedMigrationsAsync();

        Assert.Contains(applied, m => m.EndsWith("_InitialCreate"));
        Assert.Empty(await Db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void Model_has_no_changes_missing_from_migrations()
    {
        // Fails when someone edits an entity or configuration without adding a migration.
        Assert.False(Db.Database.HasPendingModelChanges());
    }
}
