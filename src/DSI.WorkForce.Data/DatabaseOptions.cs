namespace DSI.WorkForce.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Windows authentication (Integrated Security) is used everywhere, no SQL passwords.
    /// Development: Server=(localdb)\MSSQLLocalDB;Database=DSIWorkForce;Integrated Security=true;TrustServerCertificate=true
    /// </summary>
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// On startup: create the database if it does not exist, apply new migration scripts, import templates and
    /// reports, rebuild report views. On in Development; in production run DSI.WorkForce.DbMigrator instead.
    /// </summary>
    public bool AutoMigrate { get; set; }

    /// <summary>Development only: load sample presses, items and a test employee (database/SampleMasterData.sql).</summary>
    public bool SeedSampleData { get; set; }
}
