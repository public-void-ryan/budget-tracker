using System.ComponentModel.DataAnnotations;

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

    [Range(1, 365)]
    public int LifetimeDays { get; set; } = 14;

    /// <summary>Active sessions per user; logging in beyond this evicts the oldest.</summary>
    [Range(1, 1000)]
    public int MaxPerUser { get; set; } = 10;

    [Range(1, 1440)]
    public int PurgeIntervalMinutes { get; set; } = 60;
}

public sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimiting:Auth";

    public int PermitLimit { get; set; } = 10;
    public int WindowSeconds { get; set; } = 60;
}
