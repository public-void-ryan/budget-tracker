namespace BudgetTracker.Api.Auth;

/// <summary>A rule that an input broke: the field, a stable machine-readable code, a message.</summary>
public sealed record ValidationError(string Field, string Code, string Message);

/// <summary>
/// Input rules for registration credentials. Email: normalized and shape-checked, nothing
/// provider-specific (no stripping of "+tags" or dots). Password: length and a common-password
/// list only, with no composition rules.
/// </summary>
public static class CredentialRules
{
    public const int EmailMaxLength = 320;
    private const int LocalPartMaxLength = 64;
    private const int DomainMaxLength = 255;

    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;

    /// <summary>Trims and lowercases, matching the database's lowercase-email check.</summary>
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    public static IReadOnlyList<ValidationError> ValidateEmail(string? email)
    {
        var normalized = NormalizeEmail(email);

        if (normalized.Length == 0)
        {
            return [new("email", "email.required", "Email is required.")];
        }

        if (normalized.Length > EmailMaxLength)
        {
            return [new("email", "email.too_long", $"Email must be at most {EmailMaxLength} characters.")];
        }

        if (!HasValidShape(normalized))
        {
            return [new("email", "email.invalid", "Email is not a valid address.")];
        }

        return [];
    }

    public static IReadOnlyList<ValidationError> ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return [new("password", "password.required", "Password is required.")];
        }

        // Unicode code points, not UTF-16 units, so an emoji counts as one character.
        var length = password.EnumerateRunes().Count();

        if (length < PasswordMinLength)
        {
            return [new("password", "password.too_short",
                $"Password must be at least {PasswordMinLength} characters.")];
        }

        if (length > PasswordMaxLength)
        {
            return [new("password", "password.too_long",
                $"Password must be at most {PasswordMaxLength} characters.")];
        }

        if (CommonPasswords.Contains(password))
        {
            return [new("password", "password.too_common", "Password is too common.")];
        }

        return [];
    }

    public static IReadOnlyList<ValidationError> ValidateRegistration(string? email, string? password) =>
        [.. ValidateEmail(email), .. ValidatePassword(password)];

    private static bool HasValidShape(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1)
        {
            return false;
        }

        var local = email[..at];
        var domain = email[(at + 1)..];

        if (local.Length > LocalPartMaxLength || domain.Length > DomainMaxLength)
        {
            return false;
        }

        if (email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        // At least one dot, and no empty label ("a@.com", "a@b..com", "a@b.").
        var labels = domain.Split('.');
        return labels.Length >= 2 && labels.All(label => label.Length > 0);
    }
}
