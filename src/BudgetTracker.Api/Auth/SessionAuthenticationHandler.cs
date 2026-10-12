using System.Security.Claims;
using System.Text.Encodings.Web;
using BudgetTracker.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

/// <summary>
/// Authenticates "Authorization: Bearer &lt;token&gt;" against the sessions table. Expired
/// sessions never authenticate, whether or not the purge has deleted their rows yet.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BudgetDbContext db,
    TimeProvider time,
    IProblemDetailsService problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Session";
    private const string Prefix = "Bearer ";

    public static string? ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? header[Prefix.Length..].Trim()
            : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = SessionTokens.Hash(token);
        if (hash is null)
        {
            return AuthenticateResult.Fail("Malformed token.");
        }

        var now = time.GetUtcNow();
        var user = await db.Sessions
            .AsNoTracking()
            .Where(s => s.TokenHash == hash && s.ExpiresAt > now)
            .Join(db.Users, s => s.UserId, u => u.Id, (_, u) => new { u.Id, u.Email })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("Unknown or expired token.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Authentication required.",
                Extensions = { ["code"] = "auth.required" },
            },
        });
    }
}
