namespace DSI.WorkForce.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public string WorkerName { get; set; } = Environment.MachineName;

    /// <summary>Folders the scanners save into. Each folder belongs to one form template.</summary>
    public List<HotFolderOptions> HotFolders { get; set; } = new();

    /// <summary>Accepted files are copied here (yyyy/MM/dd) and processed from here. This is also the PDF archive.</summary>
    public string StagingRoot { get; set; } = @"D:\DSIWorkForce\staging";
    public string DuplicatesFolder { get; set; } = @"D:\DSIWorkForce\duplicates";
    /// <summary>Aligned page images used by the review screens (web + WPF need read access).</summary>
    public string ImageRoot { get; set; } = @"D:\DSIWorkForce\images";

    public int PollIntervalSeconds { get; set; } = 5;
    /// <summary>A file must be unchanged for this long before it is picked up (scanner still writing).</summary>
    public int FileStableSeconds { get; set; } = 5;

    /// <summary>Pages processed in parallel. 4-8 is plenty for 5,000 sheets/day; keep below CPU core count.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;
    public int BatchSize { get; set; } = 16;
    public int LeaseMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    public int MasterDataRefreshMinutes { get; set; } = 5;
}

public sealed class HotFolderOptions
{
    public string Path { get; set; } = "";
    public string TemplateCode { get; set; } = "PC-011-A";
    public string? Station { get; set; }
}
