namespace DSI.WorkForce.Core.Licensing;

public enum LicenseType
{
    /// <summary>Evaluation for a number of days.</summary>
    Trial = 0,
    /// <summary>Paid for a period (e.g. 365 days), renewed with a new key.</summary>
    Subscription = 1,
    /// <summary>One-time purchase, never expires.</summary>
    Perpetual = 2,
}

/// <summary>The signed content of a license key. Created only by the Helao2 license generator.</summary>
public sealed record LicenseInfo
{
    public int V { get; init; } = 1;
    public Guid LicenseId { get; init; } = Guid.NewGuid();
    public string Product { get; init; } = LicenseCodec.ProductCode;
    public required string LicensedTo { get; init; }
    public string? Email { get; init; }
    public LicenseType Type { get; init; }
    public DateTime IssuedUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Null = never expires (Perpetual).</summary>
    public DateTime? ExpiresUtc { get; init; }
    /// <summary>Optional: the key only works on the installation with this id (shown on the activation page).</summary>
    public string? InstallationId { get; init; }
    /// <summary>Optional: maximum number of active user accounts (null = unlimited).</summary>
    public int? MaxUsers { get; init; }
    public string? Notes { get; init; }
}

public enum LicenseStatus
{
    Valid,
    Missing,
    Invalid,
    Expired,
    WrongProduct,
    WrongInstallation,
    ClockTampered,
}

public sealed record LicenseCheck(LicenseStatus Status, LicenseInfo? License, string Message)
{
    public bool IsValid => Status == LicenseStatus.Valid;

    public int? DaysLeft => License?.ExpiresUtc is { } e ? Math.Max(0, (int)Math.Ceiling((e - DateTime.UtcNow).TotalDays)) : null;
}
