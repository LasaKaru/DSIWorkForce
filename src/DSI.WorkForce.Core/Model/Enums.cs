namespace DSI.WorkForce.Core.Model;

/// <summary>Logical data type of a captured field. Decides which typed column in dsi.WorksheetValue holds the value.</summary>
public enum FieldDataType
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    Date = 3,
    Time = 4,
    Checkbox = 5,
}

/// <summary>How a field is read from the scanned page.</summary>
public enum ExtractorKind
{
    /// <summary>Tick box: dark-pixel density inside the box (no AI).</summary>
    Omr = 0,
    /// <summary>A row of tick cells; value = ticked cells x unitsPerTick (the hourly production grid).</summary>
    OmrTally = 1,
    /// <summary>Handwritten text / numbers, read by the configured handwriting recognizer.</summary>
    Handwriting = 2,
    /// <summary>Never auto-read; always typed by a reviewer (image snippet is still shown).</summary>
    Manual = 3,
    /// <summary>Not read from the page: calculated from other fields (e.g. hourly units = sum of the 12 cells of that hour).</summary>
    Computed = 4,
}

public enum WorksheetStatus
{
    NeedsReview = 0,
    AutoApproved = 1,
    Verified = 2,
    Rejected = 3,
}

public enum ScanFileStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Duplicate = 4,
}

public enum ValueSource
{
    Omr = 0,
    Ocr = 1,
    Lookup = 2,
    Manual = 3,
    Computed = 4,
}

public enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
