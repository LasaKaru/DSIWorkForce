using DSI.WorkForce.Core.Model;

namespace DSI.WorkForce.Core.Templates;

/// <summary>
/// A paper form layout (e.g. PC-011-A v1) described as data. Stored as JSON in templates/forms and in dsi.FormTemplate.
/// Adding/moving a box on the paper form = editing this JSON, not the C# code.
/// </summary>
public sealed class FormTemplateDefinition
{
    public string TemplateCode { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    public AlignmentSettings Alignment { get; set; } = new();
    public OmrSettings Omr { get; set; } = new();

    /// <summary>Minimum confidence (0..1) every field must reach for a sheet to be auto-approved.</summary>
    public decimal AutoApproveConfidence { get; set; } = 0.90m;

    public List<FieldDefinition> Fields { get; set; } = new();
    public List<RuleDefinition> Rules { get; set; } = new();

    public FieldDefinition? Find(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

public sealed class AlignmentSettings
{
    /// <summary>OuterBorder = detect the thick printed border of the form (works on today's PC-011-A).
    /// Fiducials = 4 black corner squares (recommended for the next print run).</summary>
    public string Method { get; set; } = "OuterBorder";

    /// <summary>Size the page is warped to. All zones are fractions (0..1) of this canvas.</summary>
    public int CanonicalWidth { get; set; } = 1800;
    public int CanonicalHeight { get; set; } = 2642;

    /// <summary>The detected border must cover at least this fraction of the page, otherwise the sheet is flagged.</summary>
    public double MinBorderAreaRatio { get; set; } = 0.30;

    /// <summary>Render DPI for PDF pages.</summary>
    public int RenderDpi { get; set; } = 200;
}

public sealed class OmrSettings
{
    public int BinarizeBlockSize { get; set; } = 31;
    public double BinarizeC { get; set; } = 15;
    /// <summary>Fraction of the cell trimmed on every side so printed grid lines are not counted as ink.</summary>
    public double CellInset { get; set; } = 0.15;
    /// <summary>Ink ratio above which a box is considered marked.</summary>
    public double MarkThreshold { get; set; } = 0.015;
    /// <summary>Ratios within +/- this band around the threshold are "uncertain" and sent to review.</summary>
    public double UncertainBand { get; set; } = 0.008;
}

/// <summary>Rectangle as fractions of the aligned (canonical) page. x,y = top-left.</summary>
public sealed record Zone(double X, double Y, double W, double H)
{
    /// <summary>Returns row <paramref name="rowIndex"/> (1-based) of a zone split evenly into <paramref name="rows"/>.</summary>
    public Zone Row(int rowIndex, int rows) => rows <= 1 ? this : this with { Y = Y + H * (rowIndex - 1) / rows, H = H / rows };

    /// <summary>Returns column <paramref name="colIndex"/> (1-based) of a zone split evenly into <paramref name="cols"/>.</summary>
    public Zone Col(int colIndex, int cols) => cols <= 1 ? this : this with { X = X + W * (colIndex - 1) / cols, W = W / cols };
}

public sealed class FieldDefinition
{
    /// <summary>Stable key (e.g. EPF_NO). Never rename once data exists; the label can change freely.</summary>
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Group { get; set; }
    public FieldDataType Type { get; set; } = FieldDataType.Text;
    public ExtractorKind Extractor { get; set; } = ExtractorKind.Handwriting;
    public Zone Zone { get; set; } = new(0, 0, 0, 0);

    /// <summary>Number of rows when the zone is a table column (hourly grid, mould log). 1 = single value.</summary>
    public int Rows { get; set; } = 1;
    /// <summary>
    /// Cells per row. &gt; 1 turns the zone into a grid (rows x columns). For Handwriting fields every cell is a
    /// separate value stored with RowIndex = (row-1) * Columns + column. For OmrTally the cells are tick boxes.
    /// </summary>
    public int Columns { get; set; } = 1;
    /// <summary>Computed only: key of a grid field whose cells are summed per row.</summary>
    public string? SumOf { get; set; }
    /// <summary>OmrTally only: units represented by one tick (default; may be overridden by Machine.UnitsPerTick).</summary>
    public decimal UnitsPerTick { get; set; } = 1;
    /// <summary>Per-field OMR threshold override (e.g. signature-present detection needs more ink).</summary>
    public double? MarkThreshold { get; set; }

    public bool Required { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public List<string>? AllowedValues { get; set; }
    /// <summary>Accepted date formats, tried in order (operators write yy/MM/dd on PC-011-A).</summary>
    public List<string>? DateFormats { get; set; }
    /// <summary>Master table to validate / correct against: Employee, Machine, Item.</summary>
    public string? Lookup { get; set; }
    /// <summary>Copies the (normalized) value into an indexed column of dsi.Worksheet: WorkDate, Shift, ResourceNo, EpfNo, ItemCode.</summary>
    public string? PromoteTo { get; set; }
    /// <summary>Hint text given to AI recognizers (e.g. "Press number like P-24").</summary>
    public string? Hint { get; set; }

    public bool IsMultiRow => Rows > 1;

    /// <summary>A grid of individually stored cells (e.g. the 8 x 12 hourly production numbers).</summary>
    public bool IsGrid => Columns > 1 && Extractor is not ExtractorKind.OmrTally and not ExtractorKind.Computed;

    /// <summary>RowIndex used to store cell (row, column) of a grid field; both 1-based.</summary>
    public int CellIndex(int row, int column) => (row - 1) * Columns + column;

    public (int Row, int Column) CellOf(int rowIndex) => ((rowIndex - 1) / Columns + 1, (rowIndex - 1) % Columns + 1);

    /// <summary>Zone of one stored value: whole field, one row, or one grid cell.</summary>
    public Zone ZoneOf(int rowIndex)
    {
        if (IsGrid && rowIndex > 0)
        {
            var (r, c) = CellOf(rowIndex);
            return Zone.Row(r, Rows).Col(c, Columns);
        }
        return Zone.Row(rowIndex == 0 ? 1 : rowIndex, Rows);
    }

    /// <summary>All RowIndex values this field produces: 0, or 1..Rows, or 1..Rows*Columns for grids.</summary>
    public IEnumerable<int> RowIndexes() =>
        IsGrid ? Enumerable.Range(1, Rows * Columns) : IsMultiRow ? Enumerable.Range(1, Rows) : [0];
}

/// <summary>Cross-field business rule. Type decides which properties are used.</summary>
public sealed class RuleDefinition
{
    /// <summary>TallyMatchesTotal | SumMatchesTotal</summary>
    public string Type { get; set; } = "";
    public string? Field { get; set; }
    public string? TotalField { get; set; }
    public decimal Tolerance { get; set; }
    public IssueSeverity Severity { get; set; } = IssueSeverity.Error;
    public string? Message { get; set; }
}
