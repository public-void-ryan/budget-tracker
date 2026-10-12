using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BudgetTracker.Api.Text;

namespace BudgetTracker.Api.Imports;

/// <summary>A row with the identity used to recognize it on a later import.</summary>
/// <param name="Hash">SHA-256 of the row's content, 64 lowercase hex characters.</param>
/// <param name="Sequence">
/// Which occurrence of identical rows this is within its file, from 0. Two real $4.50 coffees on
/// one day have the same hash and sequences 0 and 1.
/// </param>
public sealed record HashedRow(ParsedRow Row, string Hash, short Sequence);

public enum RowStatus
{
    New,
    Duplicate,
}

public sealed record ClassifiedRow(HashedRow Hashed, RowStatus Status);

/// <summary>
/// Recognizing rows that were imported before. The pair (hash, sequence) is stored with every
/// imported transaction under a unique key per account, so importing an overlapping statement
/// adds only what is new.
///
/// The canonical form below is a contract with data already in the database: changing how it is
/// built changes every hash, and re-imports would stop matching earlier imports. The golden
/// tests pin it so that change has to be deliberate.
/// </summary>
public static class ImportDedup
{
    /// <summary>
    /// The text that is hashed: ISO date, the amount to four decimals, and the description with
    /// case and Unicode composition ignored. The amount is exactly what the CSV said, before any
    /// sign inversion, so changing an account's sign setting later does not make a re-import look
    /// like new transactions. The description comes last, so the fields cannot be confused with
    /// one another even if it contains the separator.
    /// </summary>
    public static string Canonical(DateOnly date, decimal amount, string description) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{date:yyyy-MM-dd}|{amount.ToString("F4", CultureInfo.InvariantCulture)}|{DescriptionText.Normalize(description)}");

    public static string Hash(ParsedRow row) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(row.Date, row.Amount, row.Description))));

    /// <summary>Hashes every row and numbers identical rows 0, 1, 2... in file order.</summary>
    public static IReadOnlyList<HashedRow> Number(IEnumerable<ParsedRow> rows)
    {
        var seen = new Dictionary<string, short>();
        var result = new List<HashedRow>();

        foreach (var row in rows)
        {
            var hash = Hash(row);
            var sequence = seen.GetValueOrDefault(hash);
            seen[hash] = checked((short)(sequence + 1));
            result.Add(new HashedRow(row, hash, sequence));
        }

        return result;
    }

    /// <summary>
    /// Marks each row Duplicate if its (hash, sequence) is already stored for the account, else
    /// New, keeping the file's order. Comparing the sequence as well as the hash is what lets a
    /// genuine second coffee through while an exact re-import is rejected.
    /// </summary>
    public static IReadOnlyList<ClassifiedRow> Classify(
        IReadOnlyList<HashedRow> rows, IReadOnlySet<(string Hash, short Sequence)> existing) =>
        [.. rows.Select(r => new ClassifiedRow(r, existing.Contains((r.Hash, r.Sequence)) ? RowStatus.Duplicate : RowStatus.New))];
}
