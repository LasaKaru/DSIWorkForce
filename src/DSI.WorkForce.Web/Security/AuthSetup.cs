using System.Security.Claims;
using DSI.WorkForce.Core.Security;
using DSI.WorkForce.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Caching.Memory;

namespace DSI.WorkForce.Web.Security;

/// <summary>
/// Sign-in with application users (dsi.AppUser) and one authorization policy per permission.
/// A user's sessions end within a minute when an admin deactivates them, changes their permissions or resets the password
/// (the "security stamp" stored in the cookie no longer matches the database).
/// </summary>
public static class AuthSetup
{
    public const string StampClaim = "dsi:stamp";

    public static IServiceCollection AddDsiAuth(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "DSIWorkForce.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/account/login";
                o.LogoutPath = "/account/logout";
                o.AccessDeniedPath = "/account/denied";
                o.ExpireTimeSpan = TimeSpan.FromHours(10);   // one shift + margin
                o.SlidingExpiration = true;
                o.Events.OnValidatePrincipal = ValidateStampAsync;
                // The JSON API (WPF client) gets status codes instead of redirects to the login page.
                o.Events.OnRedirectToLogin = ctx => ApiAware(ctx, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = ctx => ApiAware(ctx, StatusCodes.Status403Forbidden);
            });

        services.AddAuthorization(o =>
        {
            foreach (var (key, _, _) in Permissions.All)
                o.AddPolicy(key, p => p.RequireAuthenticatedUser()
                    .RequireAssertion(c => c.User.HasClaim(Permissions.ClaimType, key) || c.User.HasClaim(Permissions.ClaimType, Permissions.Admin)));
        });

        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, RevalidatingUserStateProvider>();
        return services;
    }

    public static ClaimsPrincipal CreatePrincipal(UserAuthRecord user, IEnumerable<string> permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.GivenName, user.FullName),
            new(StampClaim, user.SecurityStamp.ToString()),
        };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static bool Can(this ClaimsPrincipal user, string permission) =>
        user.Identity?.IsAuthenticated == true && (user.HasClaim(Permissions.ClaimType, permission) || user.HasClaim(Permissions.ClaimType, Permissions.Admin));

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    internal static async Task<bool> StampIsCurrentAsync(IServiceProvider sp, ClaimsPrincipal principal)
    {
        if (principal.UserId() is not { } id || principal.FindFirstValue(StampClaim) is not { } stamp) return false;
        var cache = sp.GetRequiredService<IMemoryCache>();
        var current = await cache.GetOrCreateAsync($"user-stamp:{id}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await sp.GetRequiredService<UserRepository>().GetStampAsync(id);
        });
        return current is { IsActive: true } c && c.Stamp.ToString() == stamp;
    }

    private static async Task ValidateStampAsync(CookieValidatePrincipalContext ctx)
    {
        if (ctx.Principal is null || !await StampIsCurrentAsync(ctx.HttpContext.RequestServices, ctx.Principal))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    private static Task ApiAware(RedirectContext<CookieAuthenticationOptions> ctx, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = status;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Re-checks the user every minute while a Blazor page is open (deactivated users are signed out).</summary>
public sealed class RevalidatingUserStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        if (state.User.Identity?.IsAuthenticated != true) return true;
        await using var scope = scopes.CreateAsyncScope();
        return await AuthSetup.StampIsCurrentAsync(scope.ServiceProvider, state.User);
    }
}
