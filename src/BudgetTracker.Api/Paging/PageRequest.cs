using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Paging;

public sealed record PageRequest(int Limit, TransactionCursor? After);

/// <summary>A page of results and the cursor for the next one, null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class PageParser
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    /// <summary>
    /// Above the maximum the limit is clamped, since asking for "everything" is a reasonable
    /// thing to try; below 1 is nonsense and is rejected, as is a cursor that does not decode.
    /// </summary>
    public static (PageRequest? Page, IReadOnlyList<ValidationError> Errors) Parse(int? limit, string? cursor)
    {
        var errors = new List<ValidationError>();

        if (limit is < 1)
        {
            errors.Add(new("limit", "limit.invalid", "Limit must be at least 1."));
        }

        TransactionCursor? after = null;
        if (cursor is not null)
        {
            if (TransactionCursor.TryDecode(cursor, out var decoded))
            {
                after = decoded;
            }
            else
            {
                errors.Add(new("cursor", "cursor.invalid", "Cursor is not valid."));
            }
        }

        return errors.Count > 0
            ? (null, errors)
            : (new PageRequest(Math.Min(limit ?? DefaultLimit, MaxLimit), after), errors);
    }
}
