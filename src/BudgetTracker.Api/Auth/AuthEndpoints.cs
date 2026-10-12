using System.Security.Claims;
using BudgetTracker.Api.Data;
using BudgetTracker.Api.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

public sealed record RegisterRequest(string? Email, string? Password);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record LoginResponse(string Token, string TokenType, DateTimeOffset ExpiresAt);

public sealed record UserResponse(Guid Id, string Email);

public sealed record DeleteAccountRequest(string? Password);

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    // Longer than any accepted password. Rejecting before hashing stops a huge request body
    // from being used to burn CPU in PBKDF2.
    private const int LoginPasswordMaxLength = 1024;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", Register)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("Register")
            .WithSummary("Create an account.")
            .WithDescription("404 when registration is switched off (the public demo). 409 if the email is taken.")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/login", Login)
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("Login")
            .WithSummary("Log in and receive a bearer token.")
            .WithDescription("An unknown email and a wrong password return the same 401.")
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", Logout)
            .RequireAuthorization()
            .WithName("Logout")
            .WithSummary("End the session this token belongs to.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var me = app.MapGroup("/me").WithTags("Account");

        me.MapGet("", Me)
            .RequireAuthorization()
            .WithName("GetMe")
            .WithSummary("The authenticated user.")
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // Shares the login rate-limit budget: re-entering the password makes this a password
        // oracle, and a stolen session token must not buy unlimited guesses.
        me.MapDelete("", DeleteMe)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("DeleteMe")
            .WithSummary("Permanently delete the account and everything it owns.")
            .WithDescription(
                "Requires the password in a JSON body. Removes accounts, categories, transactions, "
                + "budgets and every session. 403 if the password is wrong. Some clients and proxies "
                + "drop request bodies on DELETE, which fails here as a 400.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

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

    private static async Task<IResult> DeleteMe(
        [FromBody] DeleteAccountRequest request,
        ClaimsPrincipal principal,
        BudgetDbContext db,
        IPasswordService passwords,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Password))
        {
            return Problems.Validation([new("password", "password.required", "Password is required.")]);
        }

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var storedHash = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.PasswordHash)
            .SingleOrDefaultAsync(ct);

        if (storedHash is null)
        {
            // Deleted between authentication and here (for example a second DELETE in flight).
            return Problems.AuthenticationRequired();
        }

        if (request.Password.Length > LoginPasswordMaxLength
            || passwords.Verify(storedHash, request.Password) == PasswordVerification.Invalid)
        {
            return Problems.IncorrectPassword();
        }

        // One statement. The foreign keys' ON DELETE CASCADE remove every row the user owns:
        // accounts, categories, transactions, budgets and sessions, so the token that made
        // this request stops working the moment it returns.
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
