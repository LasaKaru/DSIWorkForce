using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Core.Security;
using DSI.WorkForce.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace DSI.WorkForce.Web.Security;

/// <summary>Sign-in / sign-out / password change. Cookies can only be set by real HTTP requests, so these are form posts.</summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/login", async ([FromForm] string userName, [FromForm] string password, [FromForm] string? returnUrl,
            UserRepository users, HttpContext ctx, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Account");
            LoginOutcome outcome; UserAuthRecord? user; IReadOnlyList<string> perms;
            try { (outcome, user, perms) = await users.LoginAsync(userName ?? "", password ?? ""); }
            catch (Exception ex)
            {
                log.LogError(ex, "Sign-in failed: database error");
                return Results.Redirect($"/account/login?error={Uri.EscapeDataString("The server cannot reach the database. Try again or contact your administrator.")}");
            }
            if (outcome != LoginOutcome.Success)
            {
                log.LogWarning("Failed sign-in for {User}: {Outcome}", userName, outcome);
                var msg = outcome switch
                {
                    LoginOutcome.LockedOut => "Too many failed attempts. Try again in 15 minutes.",
                    LoginOutcome.Inactive => "This account is disabled. Contact your administrator.",
                    _ => "Wrong user name or password.",
                };
                return Results.Redirect($"/account/login?error={Uri.EscapeDataString(msg)}&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
            }
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreatePrincipal(user!, perms));
            log.LogInformation("{User} signed in", user!.UserName);
            return Results.LocalRedirect(user.MustChangePassword ? "/account/change-password" : SafeLocal(returnUrl));
        });

        app.MapPost("/account/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/account/login");
        });

        app.MapPost("/account/change-password", async ([FromForm] string currentPassword, [FromForm] string newPassword, [FromForm] string confirmPassword,
            UserRepository users, HttpContext ctx) =>
        {
            if (ctx.User.UserId() is not { } id) return Results.Redirect("/account/login");
            string? error = newPassword != confirmPassword ? "The new passwords do not match." : PasswordHasher.Validate(newPassword, ctx.User.Identity?.Name);
            if (error is null && !await users.ChangeOwnPasswordAsync(id, currentPassword ?? "", newPassword)) error = "The current password is wrong.";
            if (error is not null) return Results.Redirect($"/account/change-password?error={Uri.EscapeDataString(error)}");

            // The password change rotated the security stamp: sign in again with fresh claims.
            var (_, user, perms) = await users.LoginAsync(ctx.User.Identity!.Name!, newPassword);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreatePrincipal(user!, perms));
            return Results.LocalRedirect("/?passwordChanged=1");
        }).RequireAuthorization();

        // JSON sign-in for the WPF desktop client (cookie is kept by its HttpClient).
        app.MapPost("/api/auth/login", async (LoginRequest req, UserRepository users, HttpContext ctx) =>
        {
            var (outcome, user, perms) = await users.LoginAsync(req.UserName, req.Password);
            if (outcome != LoginOutcome.Success) return Results.Json(new { error = outcome.ToString() }, statusCode: StatusCodes.Status401Unauthorized);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreatePrincipal(user!, perms));
            return Results.Ok(new CurrentUserDto(user!.UserId, user.UserName, user.FullName, perms, user.MustChangePassword));
        }).DisableAntiforgery();

        // Branding for the desktop client's sign-in and status bar (no secrets: just display switches).
        app.MapGet("/api/auth/branding", (DSI.WorkForce.Data.BrandingService branding, CancellationToken ct) => branding.GetAsync(ct)).AllowAnonymous();

        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).DisableAntiforgery();
    }

    private static string SafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
