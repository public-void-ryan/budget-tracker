using System.Security.Claims;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Categories;

public sealed record CategoryRequest(string? Name);

public sealed record CategoryResponse(Guid Id, string Name);

public static class CategoryEndpoints
{
    public const int NameMaxLength = 100;

    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/categories").WithTags("Categories").RequireAuthorization();

        categories.MapGet("", List)
            .WithName("ListCategories")
            .WithSummary("Your categories, ordered by name.")
            .WithDescription("Not paginated: a user has a handful of categories, not thousands.")
            .Produces<List<CategoryResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        categories.MapPost("", Create)
            .WithName("CreateCategory")
            .WithSummary("Create a category.")
            .Produces<CategoryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        categories.MapGet("/{id:guid}", Get)
            .WithName("GetCategory")
            .WithSummary("One category.")
            .WithDescription("404 for a category that does not exist and for one that belongs to someone else.")
            .Produces<CategoryResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        categories.MapPut("/{id:guid}", Update)
            .WithName("UpdateCategory")
            .WithSummary("Rename a category.")
            .Produces<CategoryResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        categories.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteCategory")
            .WithSummary("Delete a category.")
            .WithDescription(
                "Transactions in the category are kept and become uncategorized; its budgets are deleted.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static CategoryResponse ToResponse(Category category) => new(category.Id, category.Name);

    private static async Task<IResult> List(ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var categories = await db.Categories
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryResponse(c.Id, c.Name))
            .ToListAsync(ct);

        return Results.Ok(categories);
    }

    private static async Task<IResult> Create(
        CategoryRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (name, errors) = TextRules.RequiredName(request.Name, "name", NameMaxLength);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var category = new Category { UserId = user.GetId(), Name = name };

        // A duplicate name fails on the unique index and is translated to 409 centrally.
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/categories/{category.Id}", ToResponse(category));
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var category = await db.Categories
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.UserId == userId && c.Id == id, ct);

        return category is null ? Problems.NotFound() : Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> Update(
        Guid id, CategoryRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var category = await db.Categories.SingleOrDefaultAsync(c => c.UserId == userId && c.Id == id, ct);
        if (category is null)
        {
            return Problems.NotFound();
        }

        var (name, errors) = TextRules.RequiredName(request.Name, "name", NameMaxLength);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        category.Name = name;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(category));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();

        // The foreign keys do the rest: transactions keep their row with the category nulled,
        // and the category's budgets are removed.
        var deleted = await db.Categories.Where(c => c.UserId == userId && c.Id == id).ExecuteDeleteAsync(ct);

        return deleted == 0 ? Problems.NotFound() : Results.NoContent();
    }
}
