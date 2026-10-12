namespace BudgetTracker.Api.Auth;

public sealed class RegistrationOptions
{
    public const string Section = "Registration";

    /// <summary>Off unless explicitly enabled: the public demo must not accept signups.</summary>
    public bool Enabled { get; set; }
}

public sealed class AuthSessionOptions
{
    public const string Section = "Sessions";

    public int LifetimeDays { get; set; } = 14;
}

public sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
}
