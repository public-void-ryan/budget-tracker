using System.Security.Claims;

namespace BudgetTracker.Api.Auth;

public static class CurrentUser
{
    /// <summary>The authenticated user's id. Only valid on endpoints that require authorization.</summary>
    public static Guid GetId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
