namespace DSI.WorkForce.Core.Matching;

public sealed record EmployeeMaster(string EpfNo, string Name);
public sealed record MachineMaster(string ResourceNo, string? Name, decimal? UnitsPerTick);
public sealed record ItemMaster(string ItemCode, string Description, decimal? StandardRatePerHour);
public sealed record ItemAliasMaster(string Alias, string ItemCode);

/// <summary>Snapshot of master tables, cached in memory by the worker and refreshed every few minutes.</summary>
public sealed class MasterDataSnapshot
{
    public IReadOnlyDictionary<string, EmployeeMaster> Employees { get; }
    public IReadOnlyDictionary<string, MachineMaster> Machines { get; }
    public IReadOnlyList<ItemMaster> Items { get; }
    public IReadOnlyDictionary<string, string> ItemAliases { get; }

    public MasterDataSnapshot(IEnumerable<EmployeeMaster> employees, IEnumerable<MachineMaster> machines,
        IEnumerable<ItemMaster> items, IEnumerable<ItemAliasMaster> aliases)
    {
        Employees = employees.GroupBy(e => e.EpfNo.Trim()).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Machines = machines.GroupBy(m => FuzzyMatcher.CanonicalCode(m.ResourceNo)).ToDictionary(g => g.Key, g => g.First());
        Items = items.ToList();
        ItemAliases = aliases.GroupBy(a => FuzzyMatcher.CanonicalItem(a.Alias)).ToDictionary(g => g.Key, g => g.First().ItemCode);
    }

    public static MasterDataSnapshot Empty { get; } = new([], [], [], []);
}
