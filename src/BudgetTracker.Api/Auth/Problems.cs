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

    public static IResult AuthenticationRequired() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication required.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.required" });

    /// <summary>
    /// 403, not 401: the session is fine, a confirmation failed. A 401 would tell clients the
    /// session is dead and prompt them to log the user out.
    /// </summary>
    public static IResult IncorrectPassword() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Incorrect password.",
        extensions: new Dictionary<string, object?> { ["code"] = "auth.incorrect_password" });

    public static IResult Conflict(string code, string title) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>Also what another user's resource looks like, so ids cannot be used to probe.</summary>
    public static IResult NotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found.",
        extensions: new Dictionary<string, object?> { ["code"] = "not_found" });
}
