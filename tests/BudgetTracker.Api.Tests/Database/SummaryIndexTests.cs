using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

public class SummaryIndexTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task A_months_summary_reads_the_date_range_from_the_index_not_the_whole_table()
    {
        // The summary groups one month of one user's transactions. It is only cheap if the
        // planner can jump to that month; otherwise it reads every transaction the user ever had.
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var bobAccount = await AddAccountAsync(bob);
        var random = new Random(11);
        Transaction Random(User user, Account account)
        {
            var transaction = NewTransaction(user, account);
            transaction.PostedOn = new DateOnly(2024, 1, 1).AddDays(random.Next(0, 1_095));
            return transaction;
        }

        Db.AddRange(Enumerable.Range(0, 5_000).Select(_ => Random(alice, aliceAccount)));
        Db.AddRange(Enumerable.Range(0, 5_000).Select(_ => Random(bob, bobAccount)));
        await Db.SaveChangesAsync();
        await Db.Database.ExecuteSqlRawAsync("ANALYZE transactions");

        var connection = Db.Database.GetDbConnection();
        await connection.OpenAsync();
        string plan;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                EXPLAIN SELECT category_id, SUM(amount) FROM transactions
                WHERE user_id = '{alice.Id}' AND posted_on >= '2025-06-01' AND posted_on < '2025-07-01' AND NOT is_transfer
                GROUP BY category_id
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }

            plan = string.Join('\n', lines);
        }
        finally
        {
            await connection.CloseAsync();
        }

        Assert.Contains("ix_transactions_user_id_posted_on_id", plan);
        Assert.DoesNotContain("Seq Scan on transactions", plan);
    }
}
