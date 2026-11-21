using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Data;

public static class DatabaseStartupExtensions
{
    /// <summary>Runs <see cref="DatabaseInitializer"/> when Database:AutoMigrate is true. Errors are logged, not thrown,
    /// so the app still starts and shows what is wrong (the Worker keeps retrying its work loop).</summary>
    public static async Task InitializeDatabaseIfConfiguredAsync(this IServiceProvider services, ILogger log)
    {
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.AutoMigrate) return;
        try
        {
            var templates = Path.Combine(AppContext.BaseDirectory, "templates");
            await new DatabaseInitializer(options, templates, m => log.LogInformation("{Message}", m)).RunAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Automatic database setup failed. Check Database:ConnectionString (LocalDB running? `sqllocaldb start MSSQLLocalDB`).");
        }
    }
}
