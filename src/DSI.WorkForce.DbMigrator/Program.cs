using DSI.WorkForce.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// Creates / upgrades the SQL Server database, imports form templates and report definitions, builds report views.
//
//   DSI.WorkForce.DbMigrator                       create DB if missing + migrate + import templates/reports + rebuild views
//   DSI.WorkForce.DbMigrator --force-reports       overwrite report columns from templates/reports/*.json
//   DSI.WorkForce.DbMigrator --seed-sample-data    also load sample presses / items / test employee
//   DSI.WorkForce.DbMigrator --Database:ConnectionString="Server=...;Database=...;..."

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args.Where(a => a.Contains('=')).ToArray())
    .Build();

var dbOptions = config.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (string.IsNullOrWhiteSpace(dbOptions.ConnectionString))
{
    Console.Error.WriteLine("Database:ConnectionString is missing (appsettings.json, env var Database__ConnectionString or --Database:ConnectionString=...).");
    return 2;
}

var templatesDir = Path.Combine(AppContext.BaseDirectory, "templates");
dbOptions.SeedSampleData |= args.Contains("--seed-sample-data");
try
{
    await new DatabaseInitializer(dbOptions, templatesDir, m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"))
        .RunAsync(forceReports: args.Contains("--force-reports"));
    return 0;
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"Cannot connect to SQL Server: {ex.Message}");
    Console.Error.WriteLine("Check the server name in the connection string, that SQL Server / LocalDB is running " +
                            "(sqllocaldb start MSSQLLocalDB), and that your Windows account has access.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
