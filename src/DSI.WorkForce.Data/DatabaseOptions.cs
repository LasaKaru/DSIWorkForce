namespace DSI.WorkForce.Data;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    /// <summary>e.g. Server=SQL01;Database=DSIWorkForce;Integrated Security=true;TrustServerCertificate=true</summary>
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 60;
}
