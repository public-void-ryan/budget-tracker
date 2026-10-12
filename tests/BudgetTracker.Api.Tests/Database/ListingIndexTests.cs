using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

public class ListingIndexTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task The_listing_index_is_ordered_newest_first()
    {
        var definitions = await Db.Database
            .SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_transactions_user_id_posted_on_id'")
            .ToListAsync();

        var definition = Assert.Single(definitions);
        Assert.Contains("(user_id, posted_on DESC, id DESC)", definition);
    }

    [Fact]
    public async Task A_keyset_page_is_served_from_the_index_without_a_sort()
    {
        // The point of keyset pagination is that page N costs the same as page 1. That only
        // holds if the planner walks the index in order instead of sorting the user's history.
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var bobAccount = await AddAccountAsync(bob);

        // Spread across a few years so ordering matters.
        var random = new Random(7);
        Transaction Random(User user, Account account)
        {
            var transaction = NewTransaction(user, account);
            transaction.PostedOn = new DateOnly(2023, 1, 1).AddDays(random.Next(0, 1_100));
            return transaction;
        }

        Db.AddRange(Enumerable.Range(0, 3_000).Select(_ => Random(alice, aliceAccount)));
        Db.AddRange(Enumerable.Range(0, 3_000).Select(_ => Random(bob, bobAccount)));
        await Db.SaveChangesAsync();
        await Db.Database.ExecuteSqlRawAsync("ANALYZE transactions");

        var plan = await ExplainAsync(
            $"""
            SELECT id FROM transactions
            WHERE user_id = '{alice.Id}'
              AND (posted_on < '2025-06-01' OR (posted_on = '2025-06-01' AND id < '{Guid.CreateVersion7()}'))
            ORDER BY posted_on DESC, id DESC
            LIMIT 50
            """);

        Assert.Contains("ix_transactions_user_id_posted_on_id", plan);
        Assert.DoesNotContain("Sort", plan);
    }

    private async Task<string> ExplainAsync(string sql)
    {
        var connection = Db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN " + sql;
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }

            return string.Join('\n', lines);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
