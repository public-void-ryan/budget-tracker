using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BudgetTracker.Api.Tests.Database;

[Collection(PostgresCollection.Name)]
public abstract class DatabaseTest(PostgresFixture fixture) : IAsyncLifetime
{
    protected BudgetDbContext Db { get; private set; } = null!;

    public async Task InitializeAsync() => Db = await fixture.CreateDatabaseAsync();

    public async Task DisposeAsync() => await Db.DisposeAsync();

    protected async Task<User> AddUserAsync(string? email = null)
    {
        var user = new User
        {
            Email = email ?? $"{Guid.NewGuid():N}@example.com",
            PasswordHash = "not-a-real-hash",
        };
        Db.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    protected async Task<Account> AddAccountAsync(User user, string name = "Checking")
    {
        var account = new Account { UserId = user.Id, Name = name };
        Db.Add(account);
        await Db.SaveChangesAsync();
        return account;
    }

    protected async Task<Category> AddCategoryAsync(User user, string name = "Food")
    {
        var category = new Category { UserId = user.Id, Name = name };
        Db.Add(category);
        await Db.SaveChangesAsync();
        return category;
    }

    protected static Transaction NewTransaction(
        User user, Account account, Category? category = null, decimal amount = -5m,
        string? importHash = null, short? importSeq = null) => new()
    {
        UserId = user.Id,
        AccountId = account.Id,
        CategoryId = category?.Id,
        PostedOn = new DateOnly(2026, 10, 1),
        Amount = amount,
        Description = "test",
        ImportHash = importHash,
        ImportSeq = importSeq,
    };

    protected async Task<Transaction> AddTransactionAsync(
        User user, Account account, Category? category = null)
    {
        var transaction = NewTransaction(user, account, category);
        Db.Add(transaction);
        await Db.SaveChangesAsync();
        return transaction;
    }

    protected async Task<Budget> AddBudgetAsync(User user, Category category, int month = 10)
    {
        var budget = new Budget
        {
            UserId = user.Id,
            CategoryId = category.Id,
            Month = new DateOnly(2026, month, 1),
            Amount = 300m,
        };
        Db.Add(budget);
        await Db.SaveChangesAsync();
        return budget;
    }

    protected static Session NewSession(
        Guid userId, byte[]? tokenHash = null, DateTimeOffset? createdAt = null, TimeSpan? lifetime = null)
    {
        var created = createdAt ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        return new Session
        {
            UserId = userId,
            TokenHash = tokenHash ?? RandomNumberGenerator.GetBytes(32),
            CreatedAt = created,
            ExpiresAt = created + (lifetime ?? TimeSpan.FromDays(14)),
        };
    }

    protected async Task<Session> AddSessionAsync(User user)
    {
        var session = NewSession(user.Id);
        Db.Add(session);
        await Db.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// Asserts the database (not application code) rejected the write, by SQLSTATE and
    /// constraint/index name. Clears the change tracker so the failed entity is not retried.
    /// </summary>
    protected async Task AssertViolationAsync(string sqlState, string constraint, Func<Task> write)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(write);
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(sqlState, postgres.SqlState);
        Assert.Equal(constraint, postgres.ConstraintName);
        Db.ChangeTracker.Clear();
    }

    protected const string UniqueViolation = "23505";
    protected const string ForeignKeyViolation = "23503";
    protected const string CheckViolation = "23514";
}
