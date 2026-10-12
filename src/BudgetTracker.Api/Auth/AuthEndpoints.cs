using System.Security.Claims;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

public sealed record RegisterRequest(string? Email, string? Password);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record LoginResponse(string Token, string TokenType, DateTimeOffset ExpiresAt);

public sealed record UserResponse(Guid Id, string Email);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    // Longer than any accepted password. Rejecting before hashing stops a huge request body
    // from being used to burn CPU in PBKDF2.
    private const int LoginPasswordMaxLength = 1024;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");
        auth.MapPost("/register", Register).RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/login", Login).RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/logout", Logout).RequireAuthorization();

        app.MapGet("/me", Me).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        BudgetDbContext db,
        IPasswordService passwords,
        IOptions<RegistrationOptions> registration,
        CancellationToken ct)
    {
        if (!registration.Value.Enabled)
        {
            return Problems.NotFound();
        }

        var errors = CredentialRules.ValidateRegistration(request.Email, request.Password);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var user = new User
        {
            Email = CredentialRules.NormalizeEmail(request.Email),
            PasswordHash = passwords.Hash(request.Password!),
        };

        // A duplicate email fails on the unique index and is translated to 409 centrally.
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Json(new UserResponse(user.Id, user.Email), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        BudgetDbContext db,
        IPasswordService passwords,
        SessionService sessions,
        CancellationToken ct)
    {
        var missing = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            missing.Add(new("email", "email.required", "Email is required."));
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            missing.Add(new("password", "password.required", "Password is required."));
        }

        if (missing.Count > 0)
        {
            return Problems.Validation(missing);
        }

        // The registration password rules are deliberately not applied here: login must not
        // reveal the policy, and must keep working for accounts created under an older one.
        if (request.Password!.Length > LoginPasswordMaxLength)
        {
            return Problems.InvalidCredentials();
        }

        var email = CredentialRules.NormalizeEmail(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            passwords.VerifyAgainstDummy(request.Password);
            return Problems.InvalidCredentials();
        }

        var verification = passwords.Verify(user.PasswordHash, request.Password);
        if (verification == PasswordVerification.Invalid)
        {
            return Problems.InvalidCredentials();
        }

        if (verification == PasswordVerification.ValidNeedsRehash)
        {
            user.PasswordHash = passwords.Hash(request.Password);
            await db.SaveChangesAsync(ct);
        }

        var (token, expiresAt) = await sessions.CreateAsync(user.Id, ct);
        return Results.Ok(new LoginResponse(token, "Bearer", expiresAt));
    }

    private static async Task<IResult> Logout(HttpContext context, SessionService sessions, CancellationToken ct)
    {
        await sessions.RevokeAsync(SessionAuthenticationHandler.ReadToken(context.Request)!, ct);
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal principal) => Results.Ok(new UserResponse(
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
        principal.FindFirstValue(ClaimTypes.Email)!));
}
