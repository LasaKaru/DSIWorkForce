using DSI.WorkForce.Data;

namespace DSI.WorkForce.Web.Security;

/// <summary>
/// Without a valid license every page redirects to /license and every API call returns 402.
/// Only the activation page, sign-in and the files they need stay reachable.
/// </summary>
public static class LicenseGate
{
    private static readonly string[] AlwaysAllowed = ["/license", "/account", "/_framework", "/_blazor", "/_content", "/health", "/img", "/api/auth", "/Error"];

    public static IApplicationBuilder UseLicenseGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        if (AlwaysAllowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase)) || Path.HasExtension(path.Value))
        {
            await next();
            return;
        }

        var license = await ctx.RequestServices.GetRequiredService<LicenseService>().GetAsync(ctx.RequestAborted);
        if (license.IsValid)
        {
            await next();
            return;
        }

        if (path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await ctx.Response.WriteAsJsonAsync(new { error = "license", message = license.Message, contact = LicenseService.SupportEmail });
            return;
        }
        ctx.Response.Redirect("/license");
    });
}
