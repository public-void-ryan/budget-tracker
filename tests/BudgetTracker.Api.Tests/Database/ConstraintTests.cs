using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Database;

public class ConstraintTests(PostgresFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task User_email_is_unique()
    {
        await AddUserAsync("same@example.com");

        await AssertViolationAsync(UniqueViolation, "ix_users_email", async () =>
        {
            Db.Add(new User { Email = "same@example.com" });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task User_email_must_be_lowercase()
    {
        await AssertViolationAsync(CheckViolation, "ck_users_email_lowercase", async () =>
        {
            Db.Add(new User { Email = "Mixed@Example.com" });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Account_name_is_unique_per_user_but_not_across_users()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        await AddAccountAsync(alice, "Checking");

        await AddAccountAsync(bob, "Checking");

        await AssertViolationAsync(UniqueViolation, "ix_accounts_user_id_name", async () =>
        {
            Db.Add(new Account { UserId = alice.Id, Name = "Checking" });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Account_type_must_be_a_known_value()
    {
        var user = await AddUserAsync();

        await AssertViolationAsync(CheckViolation, "ck_accounts_type", async () =>
        {
            Db.Add(new Account { UserId = user.Id, Name = "Odd", Type = (AccountType)99 });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Category_name_is_unique_per_user()
    {
        var user = await AddUserAsync();
        await AddCategoryAsync(user, "Food");

        await AssertViolationAsync(UniqueViolation, "ix_categories_user_id_name", async () =>
        {
            Db.Add(new Category { UserId = user.Id, Name = "Food" });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Transaction_amount_cannot_be_zero()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_transactions_amount_nonzero", async () =>
        {
            Db.Add(NewTransaction(user, account, amount: 0m));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Transaction_amount_keeps_four_decimal_places()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);
        var transaction = NewTransaction(user, account, amount: -1234567.8912m);
        Db.Add(transaction);
        await Db.SaveChangesAsync();

        var stored = await Db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id);

        Assert.Equal(-1234567.8912m, stored.Amount);
    }

    [Fact]
    public async Task Import_hash_and_sequence_must_be_set_together()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_transactions_import_pair", async () =>
        {
            Db.Add(NewTransaction(user, account, importHash: new string('a', 64), importSeq: null));
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Reimporting_the_same_row_is_rejected()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);
        var hash = new string('a', 64);
        Db.Add(NewTransaction(user, account, importHash: hash, importSeq: 0));
        await Db.SaveChangesAsync();

        await AssertViolationAsync(
            UniqueViolation, "ix_transactions_account_id_import_hash_import_seq", async () =>
            {
                Db.Add(NewTransaction(user, account, importHash: hash, importSeq: 0));
                await Db.SaveChangesAsync();
            });
    }

    [Fact]
    public async Task Identical_rows_in_one_file_are_kept_by_sequence_number()
    {
        // Two $4.50 coffees on the same day hash identically; the sequence tells them apart.
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);
        var hash = new string('b', 64);

        Db.AddRange(
            NewTransaction(user, account, importHash: hash, importSeq: 0),
            NewTransaction(user, account, importHash: hash, importSeq: 1));
        await Db.SaveChangesAsync();

        Assert.Equal(2, await Db.Transactions.CountAsync(t => t.ImportHash == hash));
    }

    [Fact]
    public async Task The_same_import_row_is_allowed_in_different_accounts()
    {
        var user = await AddUserAsync();
        var first = await AddAccountAsync(user, "First");
        var second = await AddAccountAsync(user, "Second");
        var hash = new string('c', 64);

        Db.AddRange(
            NewTransaction(user, first, importHash: hash, importSeq: 0),
            NewTransaction(user, second, importHash: hash, importSeq: 0));
        await Db.SaveChangesAsync();

        Assert.Equal(2, await Db.Transactions.CountAsync(t => t.ImportHash == hash));
    }

    [Fact]
    public async Task Manually_entered_transactions_are_not_deduplicated()
    {
        var user = await AddUserAsync();
        var account = await AddAccountAsync(user);

        Db.AddRange(NewTransaction(user, account), NewTransaction(user, account));
        await Db.SaveChangesAsync();

        Assert.Equal(2, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Budget_amount_cannot_be_negative()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_budgets_amount_nonnegative", async () =>
        {
            Db.Add(new Budget
            {
                UserId = user.Id, CategoryId = category.Id,
                Month = new DateOnly(2026, 10, 1), Amount = -1m,
            });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Budget_month_must_be_the_first_day_of_a_month()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);

        await AssertViolationAsync(CheckViolation, "ck_budgets_month_first_day", async () =>
        {
            Db.Add(new Budget
            {
                UserId = user.Id, CategoryId = category.Id,
                Month = new DateOnly(2026, 10, 15), Amount = 100m,
            });
            await Db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task One_budget_per_category_per_month()
    {
        var user = await AddUserAsync();
        var category = await AddCategoryAsync(user);
        await AddBudgetAsync(user, category, month: 10);

        await AddBudgetAsync(user, category, month: 11);

        await AssertViolationAsync(
            UniqueViolation, "ix_budgets_user_id_category_id_month", () => AddBudgetAsync(user, category, month: 10));
    }
}
