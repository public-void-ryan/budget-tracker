using System.Globalization;
using System.Security.Claims;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Paging;
using BudgetTracker.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Transactions;

/// <param name="AccountId">An account of yours that is not archived.</param>
/// <param name="CategoryId">An optional category of yours; null for uncategorized.</param>
/// <param name="PostedOn">The date, as yyyy-MM-dd.</param>
/// <param name="Amount">
/// A JSON number, signed: negative is money leaving the account. Not zero, at most 4 decimal
/// places, below 10^15 in absolute value.
/// </param>
/// <param name="Description">1 to 500 characters.</param>
/// <param name="IsTransfer">
/// A move between your own accounts. Left out of category spending and budgets. Defaults to false.
/// </param>
public sealed record TransactionRequest(
    Guid? AccountId,
    Guid? CategoryId,
    string? PostedOn,
    decimal? Amount,
    string? Description,
    bool? IsTransfer);

public sealed record TransactionResponse(
    Guid Id,
    Guid AccountId,
    Guid? CategoryId,
    DateOnly PostedOn,
    decimal Amount,
    string Description,
    bool IsTransfer,
    DateTimeOffset CreatedAt);

/// <param name="Limit">Page size: 50 by default, at most 100 (larger values are clamped).</param>
/// <param name="Cursor">The nextCursor of the previous page. Opaque.</param>
/// <param name="From">Earliest posted date to include, as yyyy-MM-dd.</param>
/// <param name="To">Latest posted date to include, as yyyy-MM-dd.</param>
/// <param name="Uncategorized">true: only transactions with no category; false: only those that have one. Cannot be combined with categoryId.</param>
public sealed record TransactionQuery(
    int? Limit,
    string? Cursor,
    Guid? AccountId,
    Guid? CategoryId,
    string? From,
    string? To,
    bool? IsTransfer,
    bool? Uncategorized = null);

public static class TransactionEndpoints
{
    public const int DescriptionMaxLength = 500;

    private const string DateFormat = "yyyy-MM-dd";

    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var transactions = app.MapGroup("/transactions").WithTags("Transactions").RequireAuthorization();

        transactions.MapGet("", List)
            .WithName("ListTransactions")
            .WithSummary("Your transactions, newest first.")
            .WithDescription(
                "Ordered by posted date then id, both descending, and paginated with a cursor: pass the "
                + "previous page's nextCursor to get the next page; nextCursor is null on the last page. "
                + "Because pages continue from a position rather than an offset, new transactions arriving "
                + "between requests never cause repeats or gaps. Filters combine with AND.")
            .Produces<Page<TransactionResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        transactions.MapPost("", Create)
            .WithName("CreateTransaction")
            .WithSummary("Add a transaction by hand.")
            .WithDescription(
                "422 if the account or category does not exist or is not yours. 409 if the account is archived.")
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        transactions.MapGet("/{id:guid}", Get)
            .WithName("GetTransaction")
            .WithSummary("One transaction.")
            .WithDescription("404 for a transaction that does not exist and for one that belongs to someone else.")
            .Produces<TransactionResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        transactions.MapPut("/{id:guid}", Update)
            .WithName("UpdateTransaction")
            .WithSummary("Replace a transaction.")
            .WithDescription(
                "Can move it to another of your accounts or clear its category (categoryId null). 409 if it "
                + "is in an archived account or is being moved into one.")
            .Produces<TransactionResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        transactions.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteTransaction")
            .WithSummary("Delete a transaction.")
            .WithDescription("409 if it is in an archived account.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static TransactionResponse ToResponse(Transaction t) =>
        new(t.Id, t.AccountId, t.CategoryId, t.PostedOn, t.Amount, t.Description, t.IsTransfer, t.CreatedAt);

    private sealed record Valid(
        Guid AccountId, Guid? CategoryId, DateOnly PostedOn, decimal Amount, string Description, bool IsTransfer);

    private static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static (Valid? Value, IReadOnlyList<ValidationError> Errors) Validate(TransactionRequest request)
    {
        var errors = new List<ValidationError>();

        if (request.AccountId is null)
        {
            errors.Add(new("accountId", "accountId.required", "Account is required."));
        }

        DateOnly postedOn = default;
        if (string.IsNullOrWhiteSpace(request.PostedOn))
        {
            errors.Add(new("postedOn", "postedOn.required", "Posted date is required."));
        }
        else if (!TryParseDate(request.PostedOn, out postedOn))
        {
            errors.Add(new("postedOn", "postedOn.invalid", "Posted date must be a real date written as yyyy-MM-dd."));
        }

        var amount = request.Amount ?? 0m;
        if (request.Amount is null)
        {
            errors.Add(new("amount", "amount.required", "Amount is required."));
        }
        else if (MoneyRules.Check(amount) is var (code, message))
        {
            errors.Add(new("amount", code, message));
        }

        var (description, descriptionErrors) =
            TextRules.RequiredText(request.Description, "description", DescriptionMaxLength);
        errors.AddRange(descriptionErrors);

        return errors.Count > 0
            ? (null, errors)
            : (new Valid(request.AccountId!.Value, request.CategoryId, postedOn, amount, description,
                request.IsTransfer ?? false), errors);
    }

    /// <summary>
    /// Whether the caller's account is archived; null if it does not exist or is not theirs. The
    /// null case is deliberately not an error here: the composite foreign key rejects it on
    /// write, which keeps the cross-user guarantee enforced by the database.
    /// </summary>
    private static async Task<bool?> IsArchivedAsync(BudgetDbContext db, Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Id == accountId)
            .Select(a => new { a.ArchivedAt })
            .SingleOrDefaultAsync(ct);

        return account is null ? null : account.ArchivedAt is not null;
    }

    private static IResult AccountArchived() => Problems.Conflict(
        "account.archived", "That account is archived; unarchive it to change its transactions.");

    private static async Task<IResult> List(
        [AsParameters] TransactionQuery query, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var errors = new List<ValidationError>();

        var (page, pageErrors) = PageParser.Parse(query.Limit, query.Cursor);
        errors.AddRange(pageErrors);

        DateOnly? from = null;
        DateOnly? to = null;
        if (query.From is not null)
        {
            if (TryParseDate(query.From, out var parsed)) from = parsed;
            else errors.Add(new("from", "from.invalid", "From must be a real date written as yyyy-MM-dd."));
        }

        if (query.To is not null)
        {
            if (TryParseDate(query.To, out var parsed)) to = parsed;
            else errors.Add(new("to", "to.invalid", "To must be a real date written as yyyy-MM-dd."));
        }

        if (from is not null && to is not null && from > to)
        {
            errors.Add(new("to", "to.before_from", "To must not be before from."));
        }

        // "No category" and "this category" cannot both hold; say so instead of returning nothing.
        if (query is { Uncategorized: true, CategoryId: not null })
        {
            errors.Add(new("uncategorized", "uncategorized.conflicts_with_category",
                "Use either uncategorized=true or categoryId, not both."));
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var userId = user.GetId();
        var rows = db.Transactions.AsNoTracking().Where(t => t.UserId == userId);

        if (query.AccountId is { } accountId)
        {
            rows = rows.Where(t => t.AccountId == accountId);
        }

        if (query.CategoryId is { } categoryId)
        {
            rows = rows.Where(t => t.CategoryId == categoryId);
        }

        if (query.Uncategorized is { } uncategorized)
        {
            rows = uncategorized ? rows.Where(t => t.CategoryId == null) : rows.Where(t => t.CategoryId != null);
        }

        if (from is { } fromDate)
        {
            rows = rows.Where(t => t.PostedOn >= fromDate);
        }

        if (to is { } toDate)
        {
            rows = rows.Where(t => t.PostedOn <= toDate);
        }

        if (query.IsTransfer is { } isTransfer)
        {
            rows = rows.Where(t => t.IsTransfer == isTransfer);
        }

        // Keyset: everything strictly after the cursor in (posted_on DESC, id DESC) order.
        if (page!.After is { } after)
        {
            var afterDate = after.PostedOn;
            var afterId = after.Id;
            rows = rows.Where(t => t.PostedOn < afterDate || (t.PostedOn == afterDate && t.Id.CompareTo(afterId) < 0));
        }

        // One extra row says whether another page exists without a count query.
        var fetched = await rows
            .OrderByDescending(t => t.PostedOn)
            .ThenByDescending(t => t.Id)
            .Take(page.Limit + 1)
            .ToListAsync(ct);

        var items = fetched.Take(page.Limit).ToList();
        var next = fetched.Count > page.Limit
            ? new TransactionCursor(items[^1].PostedOn, items[^1].Id).Encode()
            : null;

        return Results.Ok(new Page<TransactionResponse>([.. items.Select(ToResponse)], next));
    }

    private static async Task<IResult> Create(
        TransactionRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (value, errors) = Validate(request);
        if (value is null)
        {
            return Problems.Validation(errors);
        }

        var userId = user.GetId();
        if (await IsArchivedAsync(db, userId, value.AccountId, ct) == true)
        {
            return AccountArchived();
        }

        var transaction = new Transaction
        {
            UserId = userId,
            AccountId = value.AccountId,
            CategoryId = value.CategoryId,
            PostedOn = value.PostedOn,
            Amount = value.Amount,
            Description = value.Description,
            IsTransfer = value.IsTransfer,
        };

        // An account or category that is missing or not the caller's fails on the composite
        // foreign keys and is translated to 422 centrally.
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/transactions/{transaction.Id}", ToResponse(transaction));
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var transaction = await db.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct);

        return transaction is null ? Problems.NotFound() : Results.Ok(ToResponse(transaction));
    }

    private static async Task<IResult> Update(
        Guid id, TransactionRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var transaction = await db.Transactions.SingleOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct);
        if (transaction is null)
        {
            return Problems.NotFound();
        }

        var (value, errors) = Validate(request);
        if (value is null)
        {
            return Problems.Validation(errors);
        }

        // Archived accounts are frozen in both directions: nothing leaves one and nothing moves in.
        if (await IsArchivedAsync(db, userId, transaction.AccountId, ct) == true
            || await IsArchivedAsync(db, userId, value.AccountId, ct) == true)
        {
            return AccountArchived();
        }

        transaction.AccountId = value.AccountId;
        transaction.CategoryId = value.CategoryId;
        transaction.PostedOn = value.PostedOn;
        transaction.Amount = value.Amount;
        transaction.Description = value.Description;
        transaction.IsTransfer = value.IsTransfer;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(transaction));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var transaction = await db.Transactions.SingleOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct);
        if (transaction is null)
        {
            return Problems.NotFound();
        }

        if (await IsArchivedAsync(db, userId, transaction.AccountId, ct) == true)
        {
            return AccountArchived();
        }

        db.Transactions.Remove(transaction);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
