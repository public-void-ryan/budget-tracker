using System.Security.Claims;
using System.Text;
using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Imports;

/// <param name="DryRun">Preview only: classify every row and write nothing.</param>
/// <param name="DateFormat">iso (default, 2026-10-31), us (10/31/2026), eu (31/10/2026), isoslash (2026/10/31) or eudots (31.10.2026).</param>
/// <param name="DateColumn">Header of the date column, if it is not one of the usual names.</param>
/// <param name="DescriptionColumn">Header of the description column, if it is not one of the usual names.</param>
/// <param name="AmountColumn">Header of a single signed amount column.</param>
/// <param name="DebitColumn">Header of the money-out column, for files with separate debit and credit columns.</param>
/// <param name="CreditColumn">Header of the money-in column, for files with separate debit and credit columns.</param>
public sealed record ImportQuery(
    bool? DryRun,
    string? DateFormat,
    string? DateColumn,
    string? DescriptionColumn,
    string? AmountColumn,
    string? DebitColumn,
    string? CreditColumn);

public static class ImportEndpoints
{
    public const int MaxBytes = 5 * 1024 * 1024;

    private static readonly string[] DateFormatNames = Enum.GetNames<DateFormatName>();

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/accounts/{id:guid}/imports", Import)
            .WithTags("Imports")
            .RequireAuthorization()
            .Accepts<string>("text/csv", "text/plain")
            .WithName("ImportTransactions")
            .WithSummary("Import a bank's CSV export into an account.")
            .WithDescription(
                "Send the CSV as the request body (Content-Type text/csv, UTF-8). With dryRun=true nothing is "
                + "written and every row is reported as new or duplicate, with the amount as it would be stored "
                + "next to the amount in the file, so signs can be checked first. Without it the new rows are "
                + "stored, all or none. Rows already imported are recognized and skipped, so overlapping "
                + "statements and repeated uploads are safe; two genuinely identical rows in one file are both "
                + "kept. One bad row rejects the whole file with every problem listed (up to 100). Imported "
                + "transactions are uncategorized. Limits: 5 MB and 10,000 rows. 409 if the account is archived.")
            .Produces<ImportSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static IResult AccountArchived() => Problems.Conflict(
        "account.archived", "That account is archived; unarchive it before importing into it.");

    private static (ImportOptions? Options, IReadOnlyList<ValidationError> Errors) BuildOptions(ImportQuery query)
    {
        var format = DateFormatName.Iso;
        if (query.DateFormat is not null)
        {
            var match = DateFormatNames.FirstOrDefault(n => n.Equals(query.DateFormat.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return (null, [new("dateFormat", "dateFormat.invalid",
                    $"dateFormat must be one of: {string.Join(", ", DateFormatNames.Select(n => n.ToLowerInvariant()))}.")]);
            }

            format = Enum.Parse<DateFormatName>(match);
        }

        return (new ImportOptions(
            query.DateColumn, query.DescriptionColumn, query.AmountColumn,
            query.DebitColumn, query.CreditColumn, format), []);
    }

    private static async Task<IResult> Import(
        Guid id,
        [AsParameters] ImportQuery query,
        HttpRequest request,
        ClaimsPrincipal user,
        BudgetDbContext db,
        ImportService imports,
        CancellationToken ct)
    {
        var userId = user.GetId();

        // Cheap checks first, before a large body is read.
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId && a.Id == id, ct);
        if (account is null)
        {
            return Problems.NotFound();
        }

        if (account.ArchivedAt is not null)
        {
            return AccountArchived();
        }

        var (options, optionErrors) = BuildOptions(query);
        if (options is null)
        {
            return Problems.Validation(optionErrors);
        }

        var body = await BoundedBody.ReadAsync(request.Body, request.ContentLength, MaxBytes, ct);
        if (body is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"The file is larger than {MaxBytes / (1024 * 1024)} MB.",
                extensions: new Dictionary<string, object?> { ["code"] = "import.too_large" });
        }

        string text;
        try
        {
            // Strict: bank exports in Windows-1252 or Latin-1 would otherwise turn every accent
            // into a replacement character and be stored that way.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The file is not valid UTF-8. Save it as UTF-8 and upload it again.",
                extensions: new Dictionary<string, object?> { ["code"] = "import.encoding" });
        }

        var parsed = CsvTransactionParser.Parse(new StringReader(text), options);
        if (!parsed.Succeeded)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The file has problems, so nothing was imported.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "import.invalid",
                    ["errors"] = parsed.Errors,
                    ["errorsTruncated"] = parsed.ErrorsTruncated,
                });
        }

        var result = await imports.RunAsync(userId, id, parsed.Rows, query.DryRun == true, ct);

        return result.Outcome switch
        {
            ImportOutcome.AccountNotFound => Problems.NotFound(),
            ImportOutcome.AccountArchived => AccountArchived(),
            _ => Results.Ok(result.Summary),
        };
    }
}
