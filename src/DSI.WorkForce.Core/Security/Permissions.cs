namespace DSI.WorkForce.Core.Security;

/// <summary>What a user may do. Ticked per user on Admin > Users. "Admin" implies every permission.</summary>
public static class Permissions
{
    public const string Admin = "Admin";
    public const string Scan = "Scan";
    public const string Review = "Review";
    public const string Reports = "Reports";
    public const string MasterData = "MasterData";
    public const string ReportDesign = "ReportDesign";
    public const string Users = "Users";
    public const string License = "License";

    public const string ClaimType = "dsi:perm";

    public static readonly IReadOnlyList<(string Key, string Title, string Description)> All =
    [
        (Admin, "Administrator", "Everything below, now and in future versions."),
        (Scan, "Scan / upload", "Upload scanned sheets (web upload page, WPF scan station)."),
        (Review, "Review & verify", "Correct OCR readings, verify, reject or re-read sheets."),
        (Reports, "Reports", "View reports and export to Excel."),
        (MasterData, "Master data", "Employees, items, presses / machines and shifts."),
        (ReportDesign, "Report columns", "Add / change report columns and rebuild report views."),
        (Users, "User management", "Create users, reset passwords, set permissions."),
        (License, "License", "Activate and renew the product license."),
    ];

    public static bool IsKnown(string p) => All.Any(a => a.Key == p);
}
