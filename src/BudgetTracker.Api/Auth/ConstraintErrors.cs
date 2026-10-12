using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BudgetTracker.Api.Auth;

public sealed record ConstraintRule(int Status, string Code, string Title);

/// <summary>
/// The one place database constraint names become API errors. The database stays the source of
/// truth, so there is no check-then-insert race: a concurrent duplicate fails on the constraint
/// and is reported here like any other.
/// </summary>
public static class ConstraintErrors
{
    public static readonly IReadOnlyDictionary<string, ConstraintRule> Table =
        new Dictionary<string, ConstraintRule>
        {
            ["ix_users_email"] = new(StatusCodes.Status409Conflict, "email.taken", "That email is already registered."),
            ["ix_accounts_user_id_name"] = new(
                StatusCodes.Status409Conflict, "account.name_taken", "You already have an account with that name."),
            ["fk_transactions_accounts_user_id_account_id"] = new(
                StatusCodes.Status422UnprocessableEntity, "account.invalid", "That account does not exist."),
            ["fk_transactions_categories_user_id_category_id"] = new(
                StatusCodes.Status422UnprocessableEntity, "category.invalid", "That category does not exist."),
            ["fk_rules_categories_user_id_category_id"] = new(
                StatusCodes.Status422UnprocessableEntity, "category.invalid", "That category does not exist."),
            ["ix_categories_user_id_name"] = new(
                StatusCodes.Status409Conflict, "category.name_taken", "You already have a category with that name."),
        };
}

public sealed class ConstraintViolationHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException { InnerException: PostgresException { ConstraintName: { } name } }
            || !ConstraintErrors.Table.TryGetValue(name, out var rule))
        {
            return false;
        }

        httpContext.Response.StatusCode = rule.Status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = rule.Status,
                Title = rule.Title,
                Extensions = { ["code"] = rule.Code },
            },
        });
    }
}
