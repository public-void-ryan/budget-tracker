using System.Security.Claims;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Accounts;

/// <param name="Name">1 to 100 characters, unique among your accounts.</param>
/// <param name="Type">Checking, Savings or Credit (case-sensitive).</param>
/// <param name="InvertSign">
/// Set for banks that export purchases as positive amounts. Defaults to false when omitted,
/// including on PUT, which replaces the whole resource.
/// </param>
public sealed record AccountRequest(string? Name, string? Type, bool? InvertSign);

public sealed record AccountResponse(Guid Id, string Name, string Type, bool InvertSign, DateTimeOffset? ArchivedAt);

public static class AccountEndpoints
{
    public const int NameMaxLength = 100;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/accounts").WithTags("Accounts").RequireAuthorization();

        accounts.MapGet("", List)
            .WithName("ListAccounts")
            .WithSummary("Your accounts, ordered by name.")
            .WithDescription("Archived accounts are left out unless includeArchived=true. Not paginated.")
            .Produces<List<AccountResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        accounts.MapPost("", Create)
            .WithName("CreateAccount")
            .WithSummary("Create an account.")
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        accounts.MapGet("/{id:guid}", Get)
            .WithName("GetAccount")
            .WithSummary("One account, archived or not.")
            .WithDescription("404 for an account that does not exist and for one that belongs to someone else.")
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        accounts.MapPut("/{id:guid}", Update)
            .WithName("UpdateAccount")
            .WithSummary("Replace an account's name, type and sign setting.")
            .WithDescription("Cannot change whether the account is archived; use DELETE and unarchive for that.")
            .Produces<AccountResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        accounts.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteAccount")
            .WithSummary("Archive an account, or with permanent=true delete it for good.")
            .WithDescription(
                "By default the account is archived: hidden from the default list and closed to new "
                + "transactions, with its history kept. Archiving an archived account changes nothing. "
                + "permanent=true deletes the account and all of its transactions, and is refused with "
                + "409 unless the account is already archived.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        accounts.MapPost("/{id:guid}/unarchive", Unarchive)
            .WithName("UnarchiveAccount")
            .WithSummary("Reopen an archived account.")
            .WithDescription("Unarchiving an account that is not archived changes nothing.")
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static AccountResponse ToResponse(Account account) =>
        new(account.Id, account.Name, account.Type.ToString(), account.InvertSign, account.ArchivedAt);

    private static (string Name, AccountType Type, IReadOnlyList<ValidationError> Errors) Validate(AccountRequest request)
    {
        var (name, nameErrors) = TextRules.RequiredText(request.Name, "name", NameMaxLength);
        var (type, typeErrors) = EnumRules.ParseExact<AccountType>(request.Type, "type");
        return (name, type, [.. nameErrors, .. typeErrors]);
    }

    private static async Task<IResult> List(
        bool? includeArchived, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var query = db.Accounts.AsNoTracking().Where(a => a.UserId == userId);
        if (includeArchived != true)
        {
            query = query.Where(a => a.ArchivedAt == null);
        }

        var accounts = await query.OrderBy(a => a.Name).ToListAsync(ct);
        return Results.Ok(accounts.Select(ToResponse));
    }

    private static async Task<IResult> Create(
        AccountRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (name, type, errors) = Validate(request);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var account = new Account
        {
            UserId = user.GetId(),
            Name = name,
            Type = type,
            InvertSign = request.InvertSign ?? false,
        };

        // A duplicate name fails on the unique index and is translated to 409 centrally.
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/accounts/{account.Id}", ToResponse(account));
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);

        return account is null ? Problems.NotFound() : Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> Update(
        Guid id, AccountRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);
        if (account is null)
        {
            return Problems.NotFound();
        }

        var (name, type, errors) = Validate(request);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        account.Name = name;
        account.Type = type;
        account.InvertSign = request.InvertSign ?? false;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> Delete(
        Guid id, bool? permanent, ClaimsPrincipal user, BudgetDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);
        if (account is null)
        {
            return Problems.NotFound();
        }

        if (permanent == true)
        {
            if (account.ArchivedAt is null)
            {
                return Problems.Conflict("account.not_archived", "Archive the account before deleting it permanently.");
            }

            // The account's transactions go with it through the foreign key's cascade.
            await db.Accounts.Where(a => a.UserId == userId && a.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        }

        // Keep the original time if it is already archived, so repeating the request is harmless.
        account.ArchivedAt ??= time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Unarchive(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);
        if (account is null)
        {
            return Problems.NotFound();
        }

        account.ArchivedAt = null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account));
    }
}
