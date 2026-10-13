namespace BudgetTracker.Api.Maintenance;

public sealed record DemoCredentials(string Email, string Password);

public static class DemoEndpoints
{
    /// <summary>
    /// For the public demo only: lets the login page fill in the shared demo account. The password
    /// is published on purpose (it protects nothing but data that is rebuilt every night), so the
    /// endpoint exists only when <c>Demo:ShowCredentials</c> is on and a demo password is set, and
    /// is otherwise a plain 404, like any other unknown path.
    /// </summary>
    public static IEndpointRouteBuilder MapDemoEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var password = configuration["Demo:Password"];
        if (!configuration.GetValue<bool>("Demo:ShowCredentials") || string.IsNullOrWhiteSpace(password))
        {
            return app;
        }

        app.MapGet("/demo", () => Results.Ok(new DemoCredentials(DemoData.Email, password)))
            .ExcludeFromDescription();
        return app;
    }
}
