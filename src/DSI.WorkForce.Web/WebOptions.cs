namespace DSI.WorkForce.Web;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    /// <summary>Windows = intranet single sign-on with AD accounts (recommended). None = no login (development only).</summary>
    public string Mode { get; set; } = "Windows";
    /// <summary>AD groups allowed to review/verify sheets, e.g. DSI\WorkForce-Reviewers. Empty = any signed-in user.</summary>
    public List<string> ReviewerRoles { get; set; } = new();
    /// <summary>AD groups allowed to change report columns.</summary>
    public List<string> AdminRoles { get; set; } = new();
}

public sealed class UploadOptions
{
    public const string SectionName = "Upload";
    /// <summary>Template code -> hot folder watched by the worker. Uploaded files are dropped there.</summary>
    public Dictionary<string, string> HotFolders { get; set; } = new();
    public long MaxFileBytes { get; set; } = 100 * 1024 * 1024;
}

public static class Policies
{
    public const string Reviewer = "Reviewer";
    public const string Admin = "Admin";
}
