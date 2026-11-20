using System.Text.Json;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Data;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

// Creates / upgrades the SQL Server database, imports form templates and report definitions, builds report views.
//
//   DSI.WorkForce.DbMigrator                       migrate + import templates + import new reports + rebuild views
//   DSI.WorkForce.DbMigrator --create-db           also CREATE DATABASE if it does not exist
//   DSI.WorkForce.DbMigrator --force-reports       overwrite report columns from templates/reports/*.json
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
void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");

// 1. Schema migrations (each script runs once, tracked in dbo.SchemaVersions)
DbUp.Engine.DatabaseUpgradeResult result;
try
{
    result = DatabaseMigrator.Migrate(dbOptions.ConnectionString, args.Contains("--create-db"), Log);
}
catch (Microsoft.Data.SqlClient.SqlException ex)
{
    Console.Error.WriteLine($"Cannot connect to SQL Server: {ex.Message}");
    Console.Error.WriteLine("Check the server name in the connection string, that SQL Server is running, and that your Windows account has access.");
    return 1;
}
if (!result.Successful)
{
    Console.Error.WriteLine($"Migration failed: {result.Error?.Message}");
    return 1;
}

var db = new SqlConnectionFactory(Options.Create(dbOptions));
var templates = new TemplateRepository(db);
var reports = new ReportRepository(db, templates);

// 2. Form templates (zones + fields). New versions are added; existing versions are never modified.
foreach (var file in Directory.EnumerateFiles(Path.Combine(templatesDir, "forms"), "*.json").Order())
{
    var t = TemplateSerializer.Deserialize(await File.ReadAllTextAsync(file));
    var added = await templates.UpsertAsync(t);
    Log($"Template {t.TemplateCode} v{t.Version}: {(added ? "imported" : "already present (fields refreshed)")} - {t.Fields.Count} fields");
}

// 3. Report definitions: imported once; afterwards they are maintained in the Admin page (or --force-reports).
var existing = (await reports.GetAllAsync()).Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach (var file in Directory.EnumerateFiles(Path.Combine(templatesDir, "reports"), "*.json").Order())
{
    var def = JsonSerializer.Deserialize<ReportDefinition>(await File.ReadAllTextAsync(file), TemplateSerializer.Options)!;
    if (existing.Contains(def.Code) && !args.Contains("--force-reports"))
    {
        Log($"Report {def.Code}: exists - keeping database version (use --force-reports to overwrite)");
        continue;
    }
    await reports.SaveAndBuildAsync(def);
    Log($"Report {def.Code}: imported, view rpt.vw_{def.Code} built");
}

// 4. Rebuild every report view (picks up field changes from new template versions)
await reports.RebuildAllAsync();
Log("All report views rebuilt. Done.");
return 0;
