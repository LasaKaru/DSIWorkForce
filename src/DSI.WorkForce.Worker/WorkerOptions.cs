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

    /// <summary>Safety-net polling. New files are normally picked up instantly (file-system events + in-process signals).</summary>
    public int PollIntervalSeconds { get; set; } = 5;
    /// <summary>A file must be unchanged for this long before it is picked up (scanner still writing).</summary>
    public int FileStableSeconds { get; set; } = 2;

    /// <summary>
    /// Pages processed at the same time (across all files, also pages of one big PDF).
    /// 0 = automatic: CPU cores / OCR threads per engine (= one page per core), between 1 and 8.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 0;
    /// <summary>Files claimed per round; new files are claimed as soon as slots free up.</summary>
    public int BatchSize { get; set; } = 16;
    /// <summary>
    /// Memory guard: new pages wait while the Worker uses more than this (MB). 0 = automatic (75% of available memory).
    /// Prevents out-of-memory crashes when a very large batch arrives.
    /// </summary>
    public int MaxMemoryMb { get; set; } = 0;
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
