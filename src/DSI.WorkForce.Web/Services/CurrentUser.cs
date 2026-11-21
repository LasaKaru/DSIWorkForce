using System.Security.Claims;

namespace DSI.WorkForce.Web.Services;

public static class CurrentUser
{
    public static string Name(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true, Name: { Length: > 0 } n } ? n : "unknown";
}
