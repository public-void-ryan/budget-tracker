using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Maintenance;

/// <summary>
/// The public demo account: a believable three months of data, rebuilt from scratch each time.
/// The current month's groceries and dining are left uncategorized on purpose, so applying the
/// rules is something a visitor can try and see happen.
/// </summary>
public static class DemoData
{
    /// <summary>Fixed: resetting deletes the user with exactly this email and nobody else.</summary>
    public const string Email = "demo@example.com";

    public static async Task ResetAsync(
        BudgetDbContext db, IPasswordService passwords, TimeProvider time, string password, CancellationToken ct)
    {
        // Deleting a user cascades to everything it owns (accounts, transactions, rules, budgets, sessions).
        await db.Users.Where(u => u.Email == Email).ExecuteDeleteAsync(ct);

        var user = new User { Email = Email, PasswordHash = passwords.Hash(password) };
        db.Users.Add(user);

        var checking = new Account { UserId = user.Id, Name = "Everyday checking", Type = AccountType.Checking };
        var card = new Account { UserId = user.Id, Name = "Visa card", Type = AccountType.Credit, InvertSign = true };
        var savings = new Account { UserId = user.Id, Name = "Savings", Type = AccountType.Savings };
        db.Accounts.AddRange(checking, card, savings);

        Category Cat(string name) => new() { UserId = user.Id, Name = name };
        var rent = Cat("Rent");
        var groceries = Cat("Groceries");
        var dining = Cat("Dining");
        var utilities = Cat("Utilities");
        var transport = Cat("Transport");
        var salary = Cat("Salary");
        db.Categories.AddRange(rent, groceries, dining, utilities, transport, salary);

        var rules = new (string Pattern, RuleMatchType Match, Category? Category, bool Transfer)[]
        {
            ("kroger", RuleMatchType.Contains, groceries, false),
            ("trader joe", RuleMatchType.Contains, groceries, false),
            ("starbucks", RuleMatchType.Contains, dining, false),
            ("chipotle", RuleMatchType.Contains, dining, false),
            ("shell oil", RuleMatchType.Contains, transport, false),
            ("city power", RuleMatchType.StartsWith, utilities, false),
            ("payment thank you", RuleMatchType.StartsWith, null, true),
        };
        db.Rules.AddRange(rules.Select((r, i) => new Rule
        {
            UserId = user.Id, Position = i, Pattern = r.Pattern, MatchType = r.Match,
            CategoryId = r.Category?.Id, MarkTransfer = r.Transfer,
        }));

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var random = new Random(42);

        for (var back = 2; back >= 0; back--)
        {
            var first = thisMonth.AddMonths(-back);
            var current = back == 0;

            DateOnly Day(int day) => DateOnly.FromDayNumber(Math.Min(first.DayNumber + day - 1, first.AddMonths(1).DayNumber - 1));
            bool Future(DateOnly d) => d > today;

            void Add(Account account, int day, decimal amount, string description, Category? category, bool transfer = false)
            {
                var posted = Day(day);
                if (Future(posted))
                {
                    return;
                }

                db.Transactions.Add(new Transaction
                {
                    UserId = user.Id, AccountId = account.Id, PostedOn = posted, Amount = amount,
                    Description = description, CategoryId = category?.Id, IsTransfer = transfer,
                });
            }

            decimal Spend(int min, int max) => -(min + random.Next(0, (max - min) * 100) / 100m);

            // The current month's groceries and dining are left for the rules to fill in.
            Category? Unless(Category c) => current ? null : c;

            Add(checking, 1, -1450m, "LANDLORD PROPERTY MGMT", rent);
            Add(checking, 15, 3200m, "ACME CORP PAYROLL", salary);
            Add(checking, 3, -94.20m - back, "CITY POWER & LIGHT", utilities);
            Add(checking, 20, -600m, "PAYMENT THANK YOU", null, transfer: true);
            Add(card, 20, 600m, "PAYMENT THANK YOU", null, transfer: true);

            foreach (var (day, store) in new[] { (2, "KROGER #55"), (7, "TRADER JOES #12"), (13, "KROGER #55"), (21, "KROGER #55") })
            {
                Add(card, day, Spend(35, 110), store, Unless(groceries));
            }

            foreach (var (day, place) in new[] { (4, "STARBUCKS #12"), (9, "CHIPOTLE 1204"), (16, "STARBUCKS #12"), (23, "CHIPOTLE 1204") })
            {
                Add(card, day, Spend(5, 28), place, Unless(dining));
            }

            Add(card, 6, Spend(30, 55), "SHELL OIL 5521", transport);
            Add(card, 18, Spend(30, 55), "SHELL OIL 5521", transport);
        }

        // Plans for this month and last. Dining is tight enough to be over budget in one of them.
        foreach (var month in new[] { thisMonth, thisMonth.AddMonths(-1) })
        {
            db.Budgets.AddRange(
                new Budget { UserId = user.Id, CategoryId = rent.Id, Month = month, Amount = 1450m },
                new Budget { UserId = user.Id, CategoryId = groceries.Id, Month = month, Amount = 400m },
                new Budget { UserId = user.Id, CategoryId = dining.Id, Month = month, Amount = month == thisMonth ? 60m : 120m },
                new Budget { UserId = user.Id, CategoryId = utilities.Id, Month = month, Amount = 100m },
                new Budget { UserId = user.Id, CategoryId = transport.Id, Month = month, Amount = 120m });
        }

        await db.SaveChangesAsync(ct);
    }
}
