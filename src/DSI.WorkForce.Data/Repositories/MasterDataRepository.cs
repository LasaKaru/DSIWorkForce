using Dapper;
using DSI.WorkForce.Core.Matching;

namespace DSI.WorkForce.Data.Repositories;

public sealed class MasterDataRepository(IDbConnectionFactory db)
{
    public async Task<MasterDataSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        using var multi = await conn.QueryMultipleAsync(new CommandDefinition("""
            SELECT EpfNo, Name FROM dsi.Employee WHERE IsActive = 1;
            SELECT ResourceNo, Name, UnitsPerTick FROM dsi.Machine WHERE IsActive = 1;
            SELECT ItemCode, Description, StandardRatePerHour FROM dsi.Item WHERE IsActive = 1;
            SELECT Alias, ItemCode FROM dsi.ItemAlias;
            """, cancellationToken: ct));
        var employees = (await multi.ReadAsync<EmployeeMaster>()).ToList();
        var machines = (await multi.ReadAsync<MachineMaster>()).ToList();
        var items = (await multi.ReadAsync<ItemMaster>()).ToList();
        var aliases = (await multi.ReadAsync<ItemAliasMaster>()).ToList();
        return new MasterDataSnapshot(employees, machines, items, aliases);
    }

    public async Task AddItemAliasAsync(string alias, string itemCode, string user, CancellationToken ct = default)
    {
        var canon = FuzzyMatcher.CanonicalItem(alias);
        if (canon.Length == 0) return;
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            IF EXISTS (SELECT 1 FROM dsi.Item WHERE ItemCode = @itemCode) AND NOT EXISTS (SELECT 1 FROM dsi.ItemAlias WHERE Alias = @canon)
                INSERT dsi.ItemAlias (Alias, ItemCode, CreatedBy) VALUES (@canon, @itemCode, @user);
            """, new { canon, itemCode, user }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ItemMaster>> SearchItemsAsync(string? text, int top, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemMaster>(new CommandDefinition("""
            SELECT TOP (@top) ItemCode, Description, StandardRatePerHour FROM dsi.Item
            WHERE IsActive = 1 AND (@text IS NULL OR Description LIKE N'%' + @text + N'%' OR ItemCode LIKE @text + N'%')
            ORDER BY Description;
            """, new { text, top }, cancellationToken: ct));
        return rows.ToList();
    }
}
