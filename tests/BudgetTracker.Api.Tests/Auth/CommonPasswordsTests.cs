using BudgetTracker.Api.Auth;

namespace BudgetTracker.Api.Tests.Auth;

public class CommonPasswordsTests
{
    [Fact]
    public void The_embedded_list_is_loaded()
    {
        // Guards against the resource going missing or empty, which would silently disable
        // the common-password rule.
        Assert.InRange(CommonPasswords.Count, 1_000, 10_000);
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("PassWord1234")]
    [InlineData("QWERTYUIOP123")]
    public void Matching_ignores_case(string password)
    {
        Assert.True(CommonPasswords.Contains(password));
    }

    [Fact]
    public void A_passphrase_is_not_on_the_list()
    {
        Assert.False(CommonPasswords.Contains("correct horse battery staple"));
    }
}
