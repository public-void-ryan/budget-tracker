using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

/// <summary>
/// Deletes go through ExecuteDeleteAsync so the database's own referential actions are what
/// is under test, not EF's client-side cascade tracking.
/// </summary>
public class DeleteBehaviorTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task Deleting_a_category_keeps_its_transactions_and_nulls_only_the_category()
    {
        // ON DELETE SET NULL (category_id) is raw SQL in the migration: a plain SET NULL on
        // the composite key would also try to null the NOT NULL user_id.
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);
        var category = await AddCategoryAsync(user);
        var transaction = await AddTransactionAsync(user, account, category);

        await Db.Categories.Where(c => c.Id == category.Id).ExecuteDeleteAsync();

        var stored = await Db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id);
        Assert.Null(stored.CategoryId);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(account.Id, stored.AccountId);
    }

    [Fact]
    public async Task Deleting_a_category_deletes_its_budgets()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);
        await AddBudgetAsync(user, category);

        await Db.Categories.Where(c => c.Id == category.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await Db.Budgets.CountAsync());
    }

    [Fact]
    public async Task Deleting_an_account_deletes_its_transactions()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);
        await AddTransactionAsync(user, account);

        await Db.Accounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_user_deletes_everything_they_own_and_nothing_else()
    {
        var alice = await AddUserAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var aliceCategory = await AddCategoryAsync(alice);
        await AddTransactionAsync(alice, aliceAccount, aliceCategory);
        await AddBudgetAsync(alice, aliceCategory);

        var bob = await AddUserAsync();
        var bobAccount = await AddAccountAsync(bob);
        await AddTransactionAsync(bob, bobAccount);

        await Db.Users.Where(u => u.Id == alice.Id).ExecuteDeleteAsync();

        Assert.Equal(1, await Db.Users.CountAsync());
        Assert.Equal(1, await Db.Accounts.CountAsync());
        Assert.Equal(0, await Db.Categories.CountAsync());
        Assert.Equal(1, await Db.Transactions.CountAsync());
        Assert.Equal(0, await Db.Budgets.CountAsync());
    }
}
