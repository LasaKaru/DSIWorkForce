namespace DSI.WorkForce.Web;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";
    /// <summary>Template code -> hot folder watched by the worker. Uploaded files are dropped there.</summary>
    public Dictionary<string, string> HotFolders { get; set; } = new();
    public long MaxFileBytes { get; set; } = 100 * 1024 * 1024;
}
