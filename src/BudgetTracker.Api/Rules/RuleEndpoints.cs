using System.Security.Claims;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Rules;

/// <param name="Pattern">1 to 200 characters, matched as literal text (never a regular expression).</param>
/// <param name="MatchType">Contains, StartsWith or Equals (case-sensitive names).</param>
/// <param name="CategoryId">The category to assign. Optional if the rule marks transfers.</param>
/// <param name="MarkTransfer">Mark matching transactions as transfers. Defaults to false.</param>
public sealed record RuleRequest(string? Pattern, string? MatchType, Guid? CategoryId, bool? MarkTransfer);

public sealed record RuleResponse(
    Guid Id, int Position, string MatchType, string Pattern, Guid? CategoryId, bool MarkTransfer);

/// <param name="Ids">Every one of your rule ids, exactly once, in the order they should be tried.</param>
public sealed record RuleOrderRequest(List<Guid>? Ids);

public sealed record RuleMatchResponse(bool Matched, RuleResponse? Rule);

public static class RuleEndpoints
{
    public const int PatternMaxLength = 200;
    public const int DescriptionMaxLength = 500;

    public static IEndpointRouteBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/rules").WithTags("Rules").RequireAuthorization();

        rules.MapGet("", List)
            .WithName("ListRules")
            .WithSummary("Your rules in the order they are tried.")
            .WithDescription("The first rule whose pattern matches a description wins. Not paginated: there are at most a few hundred.")
            .Produces<List<RuleResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        rules.MapPost("", Create)
            .WithName("CreateRule")
            .WithSummary("Add a rule at the end of your list.")
            .WithDescription(
                "A rule needs a category, a transfer mark, or both. 422 if the category is missing or not yours. "
                + "409 once you have reached the rule limit.")
            .Produces<RuleResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        rules.MapGet("/match", Match)
            .WithName("MatchRule")
            .WithSummary("Which rule would claim this description?")
            .WithDescription("Uses exactly the matching that import and apply use, so a rule can be checked before it is relied on.")
            .Produces<RuleMatchResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        rules.MapPost("/order", Reorder)
            .WithName("ReorderRules")
            .WithSummary("Set the order rules are tried in.")
            .WithDescription("Send every one of your rule ids exactly once. 422 if any is missing, repeated or not yours.")
            .Produces<List<RuleResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        rules.MapGet("/{id:guid}", Get)
            .WithName("GetRule")
            .WithSummary("One rule.")
            .WithDescription("404 for a rule that does not exist and for one that belongs to someone else.")
            .Produces<RuleResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPut("/{id:guid}", Update)
            .WithName("UpdateRule")
            .WithSummary("Replace a rule's pattern, match type and action.")
            .WithDescription("Keeps its place in the order; use POST /rules/order to move it.")
            .Produces<RuleResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        rules.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteRule")
            .WithSummary("Delete a rule.")
            .WithDescription("Transactions it already categorized are left as they are.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static RuleResponse ToResponse(Rule r) =>
        new(r.Id, r.Position, r.MatchType.ToString(), r.Pattern, r.CategoryId, r.MarkTransfer);

    private static RuleResponse ToResponse(RuleDefinition r) =>
        new(r.Id, r.Position, r.MatchType.ToString(), r.Pattern, r.CategoryId, r.MarkTransfer);

    private sealed record Valid(string Pattern, RuleMatchType MatchType, Guid? CategoryId, bool MarkTransfer);

    private static (Valid? Value, IReadOnlyList<ValidationError> Errors) Validate(RuleRequest request)
    {
        var (pattern, patternErrors) = TextRules.RequiredText(request.Pattern, "pattern", PatternMaxLength);
        var (matchType, typeErrors) = EnumRules.ParseExact<RuleMatchType>(request.MatchType, "matchType");
        var errors = new List<ValidationError>([.. patternErrors, .. typeErrors]);

        var markTransfer = request.MarkTransfer ?? false;
        if (request.CategoryId is null && !markTransfer)
        {
            errors.Add(new("action", "action.required", "A rule needs a category, a transfer mark, or both."));
        }

        return errors.Count > 0 ? (null, errors) : (new Valid(pattern, matchType, request.CategoryId, markTransfer), errors);
    }

    private static async Task<List<Rule>> OrderedAsync(BudgetDbContext db, Guid userId, bool track, CancellationToken ct)
    {
        var query = db.Rules.Where(r => r.UserId == userId);
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
    }

    private static async Task<IResult> List(ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct) =>
        Results.Ok((await OrderedAsync(db, user.GetId(), track: false, ct)).Select(ToResponse));

    private static async Task<IResult> Create(
        RuleRequest request,
        ClaimsPrincipal user,
        BudgetDbContext db,
        IOptions<RuleOptions> options,
        CancellationToken ct)
    {
        var (value, errors) = Validate(request);
        if (value is null)
        {
            return Problems.Validation(errors);
        }

        var userId = user.GetId();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // One creation at a time per user: the limit and the next position are both read from
        // what is there now, so two concurrent creates would otherwise overshoot the limit or
        // be given the same position.
        await db.AcquireUserLockAsync(userId, ct);

        var count = await db.Rules.CountAsync(r => r.UserId == userId, ct);
        if (count >= options.Value.MaxPerUser)
        {
            return Problems.Conflict("rule.limit_reached", $"You can have at most {options.Value.MaxPerUser} rules.");
        }

        var position = count == 0 ? 0 : await db.Rules.Where(r => r.UserId == userId).MaxAsync(r => r.Position, ct) + 1;
        var rule = new Rule
        {
            UserId = userId,
            CategoryId = value.CategoryId,
            Position = position,
            MatchType = value.MatchType,
            Pattern = value.Pattern,
            MarkTransfer = value.MarkTransfer,
        };

        // A category that is missing or not the caller's fails on the composite foreign key and
        // is translated to 422 centrally.
        db.Rules.Add(rule);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Created($"/rules/{rule.Id}", ToResponse(rule));
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var rule = await db.Rules.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId && r.Id == id, ct);

        return rule is null ? Problems.NotFound() : Results.Ok(ToResponse(rule));
    }

    private static async Task<IResult> Update(
        Guid id, RuleRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var rule = await db.Rules.SingleOrDefaultAsync(r => r.UserId == userId && r.Id == id, ct);
        if (rule is null)
        {
            return Problems.NotFound();
        }

        var (value, errors) = Validate(request);
        if (value is null)
        {
            return Problems.Validation(errors);
        }

        rule.Pattern = value.Pattern;
        rule.MatchType = value.MatchType;
        rule.CategoryId = value.CategoryId;
        rule.MarkTransfer = value.MarkTransfer;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(rule));
    }

    private static async Task<IResult> Delete(Guid id, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var userId = user.GetId();
        var deleted = await db.Rules.Where(r => r.UserId == userId && r.Id == id).ExecuteDeleteAsync(ct);

        return deleted == 0 ? Problems.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> Reorder(
        RuleOrderRequest request, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var requested = request.Ids ?? [];
        var userId = user.GetId();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.AcquireUserLockAsync(userId, ct);

        var rules = await OrderedAsync(db, userId, track: true, ct);
        var byId = rules.ToDictionary(r => r.Id);

        // Exactly the caller's rules, each once: nothing missing, repeated or foreign.
        if (requested.Count != rules.Count
            || requested.Distinct().Count() != requested.Count
            || requested.Any(id => !byId.ContainsKey(id)))
        {
            return Problems.Unprocessable("order.mismatch", "Send every one of your rule ids exactly once.");
        }

        for (var position = 0; position < requested.Count; position++)
        {
            byId[requested[position]].Position = position;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(requested.Select(id => ToResponse(byId[id])));
    }

    private static async Task<IResult> Match(
        string? description, ClaimsPrincipal user, BudgetDbContext db, CancellationToken ct)
    {
        var (text, errors) = TextRules.RequiredText(description, "description", DescriptionMaxLength);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var rules = await OrderedAsync(db, user.GetId(), track: false, ct);
        var matcher = new RuleMatcher(rules.Select(r =>
            new RuleDefinition(r.Id, r.Position, r.MatchType, r.Pattern, r.CategoryId, r.MarkTransfer)));

        var hit = matcher.FirstMatch(text);
        return Results.Ok(new RuleMatchResponse(hit is not null, hit is null ? null : ToResponse(hit)));
    }
}
