using DbUp;
using DbUp.Engine;

namespace DSI.WorkForce.Data;

/// <summary>Applies the numbered scripts in Scripts/*.sql exactly once each (journal: dbo.SchemaVersions).</summary>
public static class DatabaseMigrator
{
    public static DatabaseUpgradeResult Migrate(string connectionString, bool createDatabaseIfMissing, Action<string> log)
    {
        if (createDatabaseIfMissing)
            EnsureDatabase.For.SqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.Contains(".Scripts.") && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .LogScriptOutput()
            .LogTo(new CallbackLog(log))
            .Build();

        var pending = upgrader.GetScriptsToExecute();
        log($"{pending.Count} migration script(s) to apply.");
        return upgrader.PerformUpgrade();
    }

    private sealed class CallbackLog(Action<string> log) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) => log(string.Format(format, args));
        public void LogWarning(string format, params object[] args) => log("WARN " + string.Format(format, args));
        public void LogError(string format, params object[] args) => log("ERROR " + string.Format(format, args));
        public void LogError(Exception ex, string format, params object[] args) => log("ERROR " + string.Format(format, args) + " " + ex.Message);
    }
}
