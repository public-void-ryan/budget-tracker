namespace BudgetTracker.Api.Auth;

/// <summary>RFC 9457 problem responses with a stable machine-readable "code" extension.</summary>
public static class Problems
{
    public static IResult Validation(IEnumerable<ValidationError> errors)
    {
        var list = errors.ToList();
        var messages = list.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
        var codes = list.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray());

        return Results.ValidationProblem(
            messages,
            extensions: new Dictionary<string, object?> { ["codes"] = codes });
    }

    /// <summary>The same response whether the email is unknown or the password is wrong.</summary>
    public static IResult InvalidCredentials() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Invalid email or password.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.invalid_credentials" });

    public static IResult NotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found.");
}
