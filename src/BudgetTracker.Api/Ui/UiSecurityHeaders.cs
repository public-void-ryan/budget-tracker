using Microsoft.AspNetCore.StaticFiles;

namespace BudgetTracker.Api.Ui;

/// <summary>
/// Headers for the bundled web UI. The UI is plain ES modules with no inline script or style,
/// so the policy can forbid everything it does not need: an injected script has nowhere to run
/// and nowhere to send a stolen token (the UI keeps its session token in sessionStorage, which
/// any script running on the page could read).
/// </summary>
public static class UiSecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; " +
        "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static void Apply(StaticFileResponseContext context)
    {
        var headers = context.Context.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
    }
}
