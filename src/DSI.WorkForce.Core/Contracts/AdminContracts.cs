namespace DSI.WorkForce.Core.Contracts;

// Editable rows for the admin pages (mutable for two-way binding in Blazor).

public sealed class EmployeeRow
{
    public string EpfNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Section { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class MachineRow
{
    public string ResourceNo { get; set; } = "";
    public string? Name { get; set; }
    public string? Section { get; set; }
    public decimal? UnitsPerTick { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ItemRow
{
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Category { get; set; }
    public decimal? StandardRatePerHour { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ShiftRow
{
    public string ShiftCode { get; set; } = "";
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public decimal Hours { get; set; } = 8;
    public bool IsActive { get; set; } = true;
}

public sealed class UserRow
{
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public HashSet<string> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record LoginRequest(string UserName, string Password);
public sealed record CurrentUserDto(int UserId, string UserName, string FullName, IReadOnlyList<string> Permissions, bool MustChangePassword);
