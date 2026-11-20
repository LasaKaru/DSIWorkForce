using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSI.WorkForce.Desktop.Services;
using Microsoft.Win32;

namespace DSI.WorkForce.Desktop.ViewModels;

/// <summary>Shows any report (SQL view) in an Excel-like DataGrid; columns come from the server, so new report columns appear automatically.</summary>
public sealed partial class ReportViewModel(ApiClient api) : ObservableObject
{
    public ObservableCollection<ReportListItem> Reports { get; } = new();
    public Dictionary<string, string?> Formats { get; } = new();

    [ObservableProperty] private ReportListItem? _selectedReport;
    [ObservableProperty] private DateTime? _from = DateTime.Today.AddDays(-1);
    [ObservableProperty] private DateTime? _to = DateTime.Today;
    [ObservableProperty] private string? _shift;
    [ObservableProperty] private string? _resourceNo;
    [ObservableProperty] private string? _epfNo;
    [ObservableProperty] private DataView? _table;
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private async Task LoadReportsAsync()
    {
        try
        {
            Reports.Clear();
            foreach (var r in await api.GetReportsAsync()) Reports.Add(r);
            SelectedReport ??= Reports.FirstOrDefault();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SelectedReport is null) return;
        try
        {
            Status = "Loading…";
            var r = await api.RunReportAsync(SelectedReport.Code, D(From), D(To), Shift, ResourceNo, EpfNo);
            var dt = new DataTable();
            Formats.Clear();
            foreach (var c in r.Columns) { dt.Columns.Add(c.Header, typeof(object)); Formats[c.Header] = c.Format; }
            foreach (var row in r.Rows) dt.Rows.Add(row.Select(ToClr).ToArray());
            Table = dt.DefaultView;
            Status = $"{dt.Rows.Count} row(s)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedReport is null) return;
        var dlg = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = $"{SelectedReport.Title} {D(From):yyyy-MM-dd}.xlsx" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await File.WriteAllBytesAsync(dlg.FileName, await api.ExportReportAsync(SelectedReport.Code, D(From), D(To), Shift, ResourceNo, EpfNo));
            Status = "Saved " + dlg.FileName;
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static DateOnly D(DateTime? d) => DateOnly.FromDateTime(d ?? DateTime.Today);

    /// <summary>JSON values arrive as JsonElement; turn them into CLR values so formats and sorting work.</summary>
    private static object ToClr(object? v)
    {
        if (v is not JsonElement e) return v ?? DBNull.Value;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when e.GetString() is { } s && s.Length >= 19 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) => dt,
            JsonValueKind.String => e.GetString()!,
            _ => DBNull.Value,
        };
    }
}
