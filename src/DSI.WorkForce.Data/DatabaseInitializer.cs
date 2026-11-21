using System.Text.Json;
using Dapper;
using DSI.WorkForce.Core.Reporting;
using DSI.WorkForce.Core.Templates;
using DSI.WorkForce.Data.Repositories;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Data;

/// <summary>
/// Brings a database to the current version: CREATE DATABASE if missing -> migration scripts -> form templates ->
/// report definitions -> report views (-> optional sample master data). Safe to run on every start: each step is
/// idempotent. Used by the DbMigrator and (in Development) automatically by the Web app and the Worker.
/// </summary>
public sealed class DatabaseInitializer(DatabaseOptions options, string templatesDir, Action<string> log)
{
    // Web and Worker are usually started together from Visual Studio: only one may create/upgrade at a time.
    private const string MutexName = "DSIWorkForce-DatabaseInitializer";
    public const string DefaultAdminUser = "admin";
    public const string DefaultAdminPassword = "Admin@123";

    /// <param name="forceReports">Overwrite report columns from templates/reports/*.json.</param>
    /// <param name="alwaysRebuildViews">Rebuild report views even if nothing changed (the DbMigrator does this).</param>
    public Task RunAsync(bool forceReports = false, bool alwaysRebuildViews = false, CancellationToken ct = default) =>
        // A Mutex must be released by the thread that took it, so run the whole job on one dedicated thread.
        Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(false, MutexName);
            try { mutex.WaitOne(TimeSpan.FromMinutes(5)); }
            catch (AbandonedMutexException) { /* previous holder crashed: we own it now */ }
            try { RunCoreAsync(forceReports, alwaysRebuildViews, ct).GetAwaiter().GetResult(); }
            finally { mutex.ReleaseMutex(); }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private async Task RunCoreAsync(bool forceReports, bool alwaysRebuildViews, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        log($"Database: {builder.DataSource} / {builder.InitialCatalog} (Windows authentication: {builder.IntegratedSecurity})");

        // 1. Create the database if needed + schema migrations (each script runs once, journal: dbo.SchemaVersions)
        var result = DatabaseMigrator.Migrate(options.ConnectionString, createDatabaseIfMissing: true, log);
        if (!result.Successful)
            throw new InvalidOperationException($"Database migration failed: {result.Error?.Message}", result.Error);
        var changed = result.Scripts.Any();

        var db = new SqlConnectionFactory(Options.Create(options));

        // LocalDB / Express create databases with AUTO_CLOSE ON (the database shuts down between requests). Turn it off.
        await using (var c = await db.OpenAsync(ct))
            await c.ExecuteAsync(new CommandDefinition(
                "IF DATABASEPROPERTYEX(DB_NAME(), 'IsAutoClose') = 1 ALTER DATABASE CURRENT SET AUTO_CLOSE OFF;", cancellationToken: ct));
        var templates = new TemplateRepository(db);
        var reports = new ReportRepository(db, templates);

        // 2. Form templates (zones + fields). New versions are added; existing versions are never modified.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(templatesDir, "forms"), "*.json").Order())
        {
            var t = TemplateSerializer.Deserialize(await File.ReadAllTextAsync(file, ct));
            var import = await templates.UpsertAsync(t, ct);
            changed |= import != TemplateImportResult.Unchanged;
            log($"Template {t.TemplateCode} v{t.Version}: {import switch
            {
                TemplateImportResult.Inserted => "imported",
                TemplateImportResult.Updated => "layout updated",
                _ => "unchanged",
            }} - {t.Fields.Count} fields");
        }

        // 3. Report definitions: imported once; afterwards maintained in Admin > Report columns (or forceReports).
        var existing = (await reports.GetAllAsync(ct)).Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(templatesDir, "reports"), "*.json").Order())
        {
            var def = JsonSerializer.Deserialize<ReportDefinition>(await File.ReadAllTextAsync(file, ct), TemplateSerializer.Options)!;
            if (existing.Contains(def.Code) && !forceReports)
            {
                log($"Report {def.Code}: exists - keeping database version");
                continue;
            }
            await reports.SaveAndBuildAsync(def, ct);
            log($"Report {def.Code}: imported, view rpt.vw_{def.Code} built");
            changed = true;
        }

        // 4. Rebuild report views only when something changed: no DDL while the Worker is busy on a normal start.
        if (changed || alwaysRebuildViews)
        {
            await reports.RebuildAllAsync(ct);
            log("Report views rebuilt.");
        }

        // 5. Optional sample master data for development
        if (options.SeedSampleData && (changed || alwaysRebuildViews))
        {
            await using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream("DSI.WorkForce.Data.Seed.SampleMasterData.sql")
                                     ?? throw new InvalidOperationException("Seed script not embedded.");
            var sql = await new StreamReader(stream).ReadToEndAsync(ct);
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
            log("Sample master data loaded (presses, items, test employee 1234).");
        }

        // 6. First start: create the administrator account (password must be changed at first sign-in).
        if (await new UserRepository(db).EnsureDefaultAdminAsync(DefaultAdminUser, DefaultAdminPassword, ct))
            log($"Created administrator '{DefaultAdminUser}' with password '{DefaultAdminPassword}' - you must change it at first sign-in.");

        log("Database is up to date.");
    }
}
