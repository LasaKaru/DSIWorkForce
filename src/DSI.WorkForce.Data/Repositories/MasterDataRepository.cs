using Dapper;
using DSI.WorkForce.Core.Contracts;
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

    // ---------------- Admin > Master data ----------------

    public async Task<IReadOnlyList<EmployeeRow>> ListEmployeesAsync(string? search, CancellationToken ct = default) =>
        await QueryAsync<EmployeeRow>("""
            SELECT TOP (500) EpfNo, Name, Section, IsActive FROM dsi.Employee
            WHERE @s IS NULL OR EpfNo LIKE @s + N'%' OR Name LIKE N'%' + @s + N'%' ORDER BY IsActive DESC, EpfNo
            """, search, ct);

    public async Task<IReadOnlyList<MachineRow>> ListMachinesAsync(string? search, CancellationToken ct = default) =>
        await QueryAsync<MachineRow>("""
            SELECT TOP (500) ResourceNo, Name, Section, UnitsPerTick, IsActive FROM dsi.Machine
            WHERE @s IS NULL OR ResourceNo LIKE N'%' + @s + N'%' OR Name LIKE N'%' + @s + N'%' ORDER BY IsActive DESC, ResourceNo
            """, search, ct);

    public async Task<IReadOnlyList<ItemRow>> ListItemsAsync(string? search, CancellationToken ct = default) =>
        await QueryAsync<ItemRow>("""
            SELECT TOP (500) ItemCode, Description, Category, StandardRatePerHour, IsActive FROM dsi.Item
            WHERE @s IS NULL OR ItemCode LIKE @s + N'%' OR Description LIKE N'%' + @s + N'%' ORDER BY IsActive DESC, Description
            """, search, ct);

    public async Task<IReadOnlyList<ShiftRow>> ListShiftsAsync(CancellationToken ct = default) =>
        await QueryAsync<ShiftRow>("SELECT ShiftCode, StartTime, EndTime, Hours, IsActive FROM dsi.ShiftMaster ORDER BY StartTime", null, ct);

    public Task SaveEmployeeAsync(EmployeeRow r, CancellationToken ct = default) => ExecAsync("""
        MERGE dsi.Employee AS t USING (SELECT @EpfNo AS EpfNo) AS s ON t.EpfNo = s.EpfNo
        WHEN MATCHED THEN UPDATE SET Name = @Name, Section = @Section, IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (EpfNo, Name, Section, IsActive) VALUES (@EpfNo, @Name, @Section, @IsActive);
        """, Require(r, r.EpfNo, "EPF No"), ct);

    public Task SaveMachineAsync(MachineRow r, CancellationToken ct = default) => ExecAsync("""
        MERGE dsi.Machine AS t USING (SELECT @ResourceNo AS ResourceNo) AS s ON t.ResourceNo = s.ResourceNo
        WHEN MATCHED THEN UPDATE SET Name = @Name, Section = @Section, UnitsPerTick = @UnitsPerTick, IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (ResourceNo, Name, Section, UnitsPerTick, IsActive) VALUES (@ResourceNo, @Name, @Section, @UnitsPerTick, @IsActive);
        """, Require(r, r.ResourceNo, "Press / resource no"), ct);

    public Task SaveItemAsync(ItemRow r, CancellationToken ct = default) => ExecAsync("""
        MERGE dsi.Item AS t USING (SELECT @ItemCode AS ItemCode) AS s ON t.ItemCode = s.ItemCode
        WHEN MATCHED THEN UPDATE SET Description = @Description, Category = @Category, StandardRatePerHour = @StandardRatePerHour, IsActive = @IsActive, UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN INSERT (ItemCode, Description, Category, StandardRatePerHour, IsActive) VALUES (@ItemCode, @Description, @Category, @StandardRatePerHour, @IsActive);
        """, Require(r, r.ItemCode, "Item code"), ct);

    public Task SaveShiftAsync(ShiftRow r, CancellationToken ct = default) => ExecAsync("""
        MERGE dsi.ShiftMaster AS t USING (SELECT @ShiftCode AS ShiftCode) AS s ON t.ShiftCode = s.ShiftCode
        WHEN MATCHED THEN UPDATE SET StartTime = @StartTime, EndTime = @EndTime, Hours = @Hours, IsActive = @IsActive
        WHEN NOT MATCHED THEN INSERT (ShiftCode, StartTime, EndTime, Hours, IsActive) VALUES (@ShiftCode, @StartTime, @EndTime, @Hours, @IsActive);
        """, Require(r, r.ShiftCode, "Shift code"), ct);

    private static T Require<T>(T row, string key, string label) =>
        string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException($"{label} is required.") : row;

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, string? search, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<T>(new CommandDefinition(sql, new { s = string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, cancellationToken: ct))).ToList();
    }

    private async Task ExecAsync(string sql, object row, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: ct));
    }
}
