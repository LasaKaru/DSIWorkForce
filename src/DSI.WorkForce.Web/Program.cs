using DSI.WorkForce.Data;
using DSI.WorkForce.Web;
using DSI.WorkForce.Web.Api;
using DSI.WorkForce.Web.Components;
using DSI.WorkForce.Web.Security;
using DSI.WorkForce.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.FromLogContext());

builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection(UploadOptions.SectionName));
builder.Services.AddDsiData();
builder.Services.AddSingleton<UploadService>();

// Users, permissions (Admin > Users) and cookie sign-in.
builder.Services.AddDsiAuth();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Development: create the database if it does not exist and bring it up to date (production: run the DbMigrator).
await app.Services.InitializeDatabaseIfConfiguredAsync(app.Logger);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseLicenseGate();          // no valid license -> everything except /license and sign-in is locked
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAccountEndpoints();
app.MapDsiApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
