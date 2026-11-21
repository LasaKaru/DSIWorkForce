using System.Text;
using Dapper;

namespace DSI.WorkForce.Data.Repositories;

/// <summary>Troubleshooting helper: which sessions hold open transactions or block others (needs VIEW SERVER STATE).</summary>
public sealed class DiagnosticsRepository(IDbConnectionFactory db)
{
    public async Task<string> GetLockReportAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int SessionId, int? BlockedBy, string? Program, string? Host, string? Status, int OpenTran, string? WaitType, string? LastSql)>(
            new CommandDefinition("""
                SELECT s.session_id, NULLIF(r.blocking_session_id, 0), s.program_name, s.host_name, s.status,
                       s.open_transaction_count, r.wait_type, LEFT(t.text, 300)
                FROM sys.dm_exec_sessions s
                LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
                LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id
                OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t
                WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
                  AND (s.open_transaction_count > 0 OR r.blocking_session_id <> 0
                       OR s.session_id IN (SELECT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0));
                """, cancellationToken: ct));
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.AppendLine($"  session {r.SessionId} [{r.Program} @ {r.Host}] status={r.Status} openTran={r.OpenTran}" +
                          $"{(r.BlockedBy is { } b ? $" BLOCKED BY {b}" : "")} wait={r.WaitType} sql={r.LastSql?.Replace('\n', ' ').Replace('\r', ' ')}");
        return sb.Length == 0 ? "  (no blocking sessions found)" : sb.ToString();
    }
}
