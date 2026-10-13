using System.Security.Claims;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Budgets;

/// <param name="Amount">Planned spending for the category that month: zero or more, at most 4 decimal places.</param>
public sealed record BudgetRequest(decimal? Amount);

public sealed record BudgetResponse(Guid CategoryId, string CategoryName, string Month, decimal Amount);

public sealed record BudgetCopyResponse(int Copied, int SkippedExisting);

public static class BudgetEndpoints
{
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var budgets = app.MapGroup("/budgets").WithTags("Budgets").RequireAuthorization();

        budgets.MapGet("", List)
            .WithName("ListBudgets")
            .WithSummary("Your budgets for one month, ordered by category name.")
            .Produces<List<BudgetResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        budgets.MapGet("/summary", Summary)
            .WithName("BudgetSummary")
            .WithSummary("Budget versus actual for a month.")
            .WithDescription(
                "Expenses: one row per category that has a budget or spending that month, with spent "
                + "(net outflow; refunds reduce it), remaining and an over flag. Income: categories with no "
                + "budget that took in more than they paid out, shown as received. Uncategorized money out and "
                + "in each get their own figure. Totals: budgeted, spent, remaining (budgeted minus all "
                + "spent), received and net (received minus spent). Transfers are excluded; archived "
                + "accounts' transactions are included. The totals are the sum of the rows, so nothing is hidden.")
            .Produces<BudgetSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        budgets.MapPut("/{month}/{categoryId:guid}", Put)
            .WithName("SetBudget")
            .WithSummary("Set the budget for a category in a month, creating or replacing it.")
            .WithDescription(
                "Month is written yyyy-MM. 201 when created, 200 when an existing budget was replaced. "
                + "404 if the category does not exist or is not yours.")
            .Produces<BudgetResponse>(StatusCodes.Status201Created)
            .Produces<BudgetResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        budgets.MapDelete("/{month}/{categoryId:guid}", Delete)
            .WithName("DeleteBudget")
            .WithSummary("Remove the budget for a category in a month.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        budgets.MapPost("/copy", Copy)
            .WithName("CopyBudgets")
            .WithSummary("Copy one month's budgets into another.")
            .WithDescription(
                "Adds a budget for every category that has one in the source month and none in the target month. "
                + "Never overwrites a budget that already exists.")
            .Produces<BudgetCopyResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static (decimal? Amount, IReadOnlyList<ValidationError> Errors) ValidateAmount(decimal? amount)
    {
        if (amount is null)
        {
            return (null, [new("amount", "amount.required", "Amount is required.")]);
        }

        if (amount < 0)
        {
            return (null, [new("amount", "amount.negative", "A budget cannot be negative; use 0 to plan no spending.")]);
        }

        // Zero is a legitimate budget, so only the range and precision rules apply to the rest.
        if (amount != 0 && MoneyRules.Check(amount.Value) is var (code, message))
        {
            return (null, [new("amount", code, message)]);
        }

        return (amount, []);
    }

    private static async Task<IResult> List(
        [FromQuery] string? month, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (first, error) = MonthRules.Parse(month, "month");
        if (first is null)
        {
            return Problems.Validation([error!]);
        }

        var userId = user.GetId();
        var firstDay = first.Value;
        var rows = await (
            from b in db.Budgets.AsNoTracking()
            where b.UserId == userId && b.Month == firstDay
            join c in db.Categories.AsNoTracking() on new { b.UserId, Id = b.CategoryId } equals new { c.UserId, c.Id }
            orderby c.Name
            select new { b.CategoryId, CategoryName = c.Name, b.Amount }).ToListAsync(ct);

        return Results.Ok(rows.Select(r => new BudgetResponse(r.CategoryId, r.CategoryName, MonthRules.Format(firstDay), r.Amount)));
    }

    private static async Task<IResult> Summary(
        [FromQuery] string? month, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (first, error) = MonthRules.Parse(month, "month");
        if (first is null)
        {
            return Problems.Validation([error!]);
        }

        var userId = user.GetId();
        var firstDay = first.Value;
        var nextMonth = firstDay.AddMonths(1);

        var budgeted = await db.Budgets.AsNoTracking()
            .Where(b => b.UserId == userId && b.Month == firstDay)
            .ToDictionaryAsync(b => b.CategoryId, b => b.Amount, ct);

        // Half-open on dates: the first day of the month is in, the first day of the next is out.
        // Transfers are not spending, so they are left out.
        var sums = await db.Transactions.AsNoTracking()
            .Where(t => t.UserId == userId && t.PostedOn >= firstDay && t.PostedOn < nextMonth && !t.IsTransfer)
            .GroupBy(t => t.CategoryId)
            .Select(g => new
            {
                CategoryId = g.Key,
                Net = g.Sum(t => t.Amount),
                Out = g.Sum(t => t.Amount < 0 ? -t.Amount : 0m),
                In = g.Sum(t => t.Amount > 0 ? t.Amount : 0m),
            })
            .ToListAsync(ct);

        var net = sums.Where(x => x.CategoryId is not null).ToDictionary(x => x.CategoryId!.Value, x => x.Net);
        var uncategorizedOut = sums.Where(x => x.CategoryId is null).Sum(x => x.Out);
        var uncategorizedIn = sums.Where(x => x.CategoryId is null).Sum(x => x.In);

        var wanted = budgeted.Keys.Concat(net.Keys).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.UserId == userId && wanted.Contains(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new CategoryRef(c.Id, c.Name))
            .ToListAsync(ct);

        return Results.Ok(BudgetSummaryBuilder.Build(MonthRules.Format(firstDay), categories, budgeted, net, uncategorizedOut, uncategorizedIn));
    }

    private static async Task<IResult> Put(
        string month, Guid categoryId, BudgetRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (first, monthError) = MonthRules.Parse(month, "month");
        if (first is null)
        {
            return Problems.Validation([monthError!]);
        }

        var userId = user.GetId();
        var categoryName = await db.Categories
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.Id == categoryId)
            .Select(c => c.Name)
            .SingleOrDefaultAsync(ct);
        if (categoryName is null)
        {
            return Problems.NotFound();
        }

        var (amount, errors) = ValidateAmount(request.Amount);
        if (amount is null)
        {
            return Problems.Validation(errors);
        }

        // One statement, so two simultaneous requests for the same budget cannot collide on the
        // unique key: the second becomes an update. "xmax = 0" is true only for a row that was
        // just inserted, which is how a created budget is told from a replaced one.
        var firstDay = first.Value;
        var id = Guid.CreateVersion7();
        //
        // AsAsyncEnumerable stops EF wrapping the statement in a subquery to add its own LIMIT,
        // which PostgreSQL does not allow around an INSERT ... RETURNING.
        var created = await db.Database.SqlQuery<bool>($"""
            INSERT INTO budgets (id, user_id, category_id, month, amount)
            VALUES ({id}, {userId}, {categoryId}, {firstDay}, {amount.Value})
            ON CONFLICT (user_id, category_id, month) DO UPDATE SET amount = EXCLUDED.amount
            RETURNING (xmax = 0) AS "Value"
            """).AsAsyncEnumerable().SingleAsync(ct);

        var response = new BudgetResponse(categoryId, categoryName, MonthRules.Format(firstDay), amount.Value);
        return created
            ? Results.Created($"/budgets?month={MonthRules.Format(firstDay)}", response)
            : Results.Ok(response);
    }

    private static async Task<IResult> Delete(
        string month, Guid categoryId, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (first, error) = MonthRules.Parse(month, "month");
        if (first is null)
        {
            return Problems.Validation([error!]);
        }

        var userId = user.GetId();
        var firstDay = first.Value;
        var deleted = await db.Budgets
            .Where(b => b.UserId == userId && b.CategoryId == categoryId && b.Month == firstDay)
            .ExecuteDeleteAsync(ct);

        return deleted == 0 ? Problems.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> Copy(
        [FromQuery(Name = "from")] string? source,
        [FromQuery(Name = "to")] string? target,
        ClaimsPrincipal user,
        BudgetDbContext db,
        CancellationToken ct)
    {
        var (from, fromError) = MonthRules.Parse(source, "from");
        var (to, toError) = MonthRules.Parse(target, "to");
        var errors = new[] { fromError, toError }.OfType<ValidationError>().ToList();

        if (from is not null && to is not null && from == to)
        {
            errors.Add(new("to", "to.same_as_from", "The target month must differ from the source month."));
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var userId = user.GetId();
        var fromDay = from!.Value;
        var toDay = to!.Value;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Serialized per user, so two copies cannot both decide a budget is missing and insert it.
        await db.AcquireUserLockAsync(userId, ct);

        var sourceBudgets = await db.Budgets.AsNoTracking()
            .Where(b => b.UserId == userId && b.Month == fromDay)
            .Select(b => new { b.CategoryId, b.Amount })
            .ToListAsync(ct);
        var alreadyThere = (await db.Budgets.AsNoTracking()
            .Where(b => b.UserId == userId && b.Month == toDay)
            .Select(b => b.CategoryId)
            .ToListAsync(ct)).ToHashSet();

        var missing = sourceBudgets.Where(b => !alreadyThere.Contains(b.CategoryId)).ToList();
        db.Budgets.AddRange(missing.Select(b => new Budget
        {
            UserId = userId, CategoryId = b.CategoryId, Month = toDay, Amount = b.Amount,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new BudgetCopyResponse(missing.Count, sourceBudgets.Count - missing.Count));
    }
}
