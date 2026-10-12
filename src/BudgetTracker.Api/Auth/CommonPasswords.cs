using System.Reflection;

namespace BudgetTracker.Api.Auth;

/// <summary>
/// The most common passwords that are long enough to pass the length rule, loaded once from an
/// embedded resource so the check works offline and in tests.
/// </summary>
public static class CommonPasswords
{
    private static readonly Lazy<HashSet<string>> Entries = new(Load);

    public static int Count => Entries.Value.Count;

    public static bool Contains(string password) =>
        Entries.Value.Contains(password.ToLowerInvariant());

    private static HashSet<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("common-passwords.txt")
            ?? throw new InvalidOperationException("The common-passwords.txt resource is missing.");
        using var reader = new StreamReader(stream);

        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            set.Add(line);
        }

        return set;
    }
}
