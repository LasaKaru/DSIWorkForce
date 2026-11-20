using DSI.WorkForce.Data;
using DSI.WorkForce.Web;
using DSI.WorkForce.Web.Api;
using DSI.WorkForce.Web.Components;
using DSI.WorkForce.Web.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext());

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));
builder.Services.AddDsiData();
builder.Services.AddSingleton<UploadService>();

// ---- Authentication: Windows (AD) single sign-on on the factory network ----
var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
if (auth.Mode.Equals("Windows", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
    builder.Services.AddAuthorization(o =>
    {
        o.FallbackPolicy = o.DefaultPolicy;   // every page requires a signed-in user
        o.AddPolicy(Policies.Reviewer, p => { p.RequireAuthenticatedUser(); if (auth.ReviewerRoles.Count > 0) p.RequireRole(auth.ReviewerRoles.Concat(auth.AdminRoles)); });
        o.AddPolicy(Policies.Admin, p => { p.RequireAuthenticatedUser(); if (auth.AdminRoles.Count > 0) p.RequireRole(auth.AdminRoles); });
    });
}
else
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Auth:Mode=None is only allowed in Development.");
    builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(o =>
    {
        o.AddPolicy(Policies.Reviewer, p => p.RequireAssertion(_ => true));
        o.AddPolicy(Policies.Admin, p => p.RequireAssertion(_ => true));
    });
}
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapDsiApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
