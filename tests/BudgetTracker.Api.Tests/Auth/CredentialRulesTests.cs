using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Tests.Auth;

public class CredentialRulesTests
{
    private static string[] Codes(IReadOnlyList<ValidationError> errors) =>
        [.. errors.Select(e => e.Code)];

    // Emails

    [Theory]
    [InlineData("  Ryan@Example.COM ", "ryan@example.com")]
    [InlineData("a@b.co", "a@b.co")]
    [InlineData("", "")]
    public void Emails_are_trimmed_and_lowercased(string input, string expected)
    {
        Assert.Equal(expected, CredentialRules.NormalizeEmail(input));
    }

    [Fact]
    public void A_null_email_normalizes_to_empty()
    {
        Assert.Equal("", CredentialRules.NormalizeEmail(null));
    }

    [Fact]
    public void Plus_tags_and_dots_are_kept_not_stripped()
    {
        // Stripping them is provider-specific and would merge distinct addresses.
        Assert.Equal("first.last+tag@example.com", CredentialRules.NormalizeEmail("First.Last+Tag@Example.com"));
    }

    [Theory]
    [InlineData("a@b.co")]
    [InlineData("first.last+tag@sub.example.com")]
    [InlineData("UPPER@EXAMPLE.COM")]
    [InlineData("  padded@example.com  ")]
    [InlineData("ünï@exämple.com")]
    public void Plausible_emails_are_accepted(string email)
    {
        Assert.Empty(CredentialRules.ValidateEmail(email));
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@example")]
    [InlineData("us er@example.com")]
    [InlineData("a@@b.com")]
    [InlineData("a@b@c.com")]
    [InlineData("a@b..com")]
    [InlineData("a@.com")]
    [InlineData("a@b.")]
    public void Malformed_emails_are_rejected(string email)
    {
        Assert.Equal(["email.invalid"], Codes(CredentialRules.ValidateEmail(email)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_missing_email_is_reported_as_required(string? email)
    {
        Assert.Equal(["email.required"], Codes(CredentialRules.ValidateEmail(email)));
    }

    [Fact]
    public void An_email_over_the_length_limit_is_rejected()
    {
        var email = new string('a', 60) + "@" + string.Join(".", Enumerable.Repeat(new string('b', 60), 5)) + ".com";

        Assert.True(email.Length > CredentialRules.EmailMaxLength);
        Assert.Equal(["email.too_long"], Codes(CredentialRules.ValidateEmail(email)));
    }

    [Fact]
    public void The_local_part_is_limited_to_64_characters()
    {
        Assert.Empty(CredentialRules.ValidateEmail(new string('a', 64) + "@example.com"));
        Assert.Equal(
            ["email.invalid"],
            Codes(CredentialRules.ValidateEmail(new string('a', 65) + "@example.com")));
    }

    // Passwords

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_password_is_reported_as_required(string? password)
    {
        Assert.Equal(["password.required"], Codes(CredentialRules.ValidatePassword(password)));
    }

    [Fact]
    public void Eleven_characters_is_too_short_and_twelve_is_enough()
    {
        Assert.Equal(["password.too_short"], Codes(CredentialRules.ValidatePassword("zebrastripe")));
        Assert.Empty(CredentialRules.ValidatePassword("zebrastripes"));
    }

    [Fact]
    public void One_hundred_twenty_eight_characters_is_allowed_and_one_more_is_not()
    {
        Assert.Empty(CredentialRules.ValidatePassword(new string('x', 128)));
        Assert.Equal(["password.too_long"], Codes(CredentialRules.ValidatePassword(new string('x', 129))));
    }

    [Fact]
    public void Length_counts_characters_not_utf16_units()
    {
        // Each emoji is two UTF-16 units but one character.
        Assert.Empty(CredentialRules.ValidatePassword(string.Concat(Enumerable.Repeat("😀", 12))));
        Assert.Equal(
            ["password.too_short"],
            Codes(CredentialRules.ValidatePassword(string.Concat(Enumerable.Repeat("😀", 11)))));
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    public void Common_passwords_are_rejected_regardless_of_case(string password)
    {
        Assert.Equal(["password.too_common"], Codes(CredentialRules.ValidatePassword(password)));
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("alllowercaseletters")]
    [InlineData("ALLUPPERCASELETTERS")]
    [InlineData("1029384756102938")]
    public void There_are_no_composition_rules(string password)
    {
        // No required digit, symbol or mixed case: length and the common list are the policy.
        Assert.Empty(CredentialRules.ValidatePassword(password));
    }

    // Both together

    [Fact]
    public void Registration_reports_every_problem_at_once_with_the_field_it_belongs_to()
    {
        var errors = CredentialRules.ValidateRegistration("not-an-email", "short");

        Assert.Equal(
            [("email", "email.invalid"), ("password", "password.too_short")],
            errors.Select(e => (e.Field, e.Code)).ToArray());
    }

    [Fact]
    public void A_valid_registration_has_no_errors()
    {
        Assert.Empty(CredentialRules.ValidateRegistration("ryan@example.com", "correct horse battery staple"));
    }
}
