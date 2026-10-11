namespace BudgetTracker.Api.Tests.Database;

/// <summary>
/// Composite foreign keys on (user_id, ...) make cross-user references impossible at the
/// database level, even if a future endpoint forgets to check ownership.
/// </summary>
public class TenancyTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task Transaction_cannot_use_another_users_category()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var aliceAccount = await AddAccountAsync(alice);
        var bobCategory = await AddCategoryAsync(bob);

        await AssertViolationAsync(
            ForeignKeyViolation, "fk_transactions_categories_user_id_category_id", async () =>
            {
                Db.Add(NewTransaction(alice, aliceAccount, bobCategory));
                await Db.SaveChangesAsync();
            });
    }

    [Fact]
    public async Task Transaction_cannot_use_another_users_account()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var bobAccount = await AddAccountAsync(bob);

        await AssertViolationAsync(
            ForeignKeyViolation, "fk_transactions_accounts_user_id_account_id", async () =>
            {
                Db.Add(NewTransaction(alice, bobAccount));
                await Db.SaveChangesAsync();
            });
    }

    [Fact]
    public async Task Transaction_may_have_no_category()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);

        var transaction = await AddTransactionAsync(user, account, category: null);

        Assert.Null(transaction.CategoryId);
    }

    [Fact]
    public async Task Budget_cannot_use_another_users_category()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        var bobCategory = await AddCategoryAsync(bob);

        await AssertViolationAsync(
            ForeignKeyViolation, "fk_budgets_categories_user_id_category_id",
            () => AddBudgetAsync(alice, bobCategory));
    }
}
