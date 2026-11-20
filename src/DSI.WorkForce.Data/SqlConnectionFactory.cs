using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DSI.WorkForce.Data;

public interface IDbConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
    int CommandTimeout { get; }
}

public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options) : IDbConnectionFactory
{
    private readonly DatabaseOptions _options = options.Value;

    public int CommandTimeout => _options.CommandTimeoutSeconds;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("Database:ConnectionString is not configured.");
        var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
