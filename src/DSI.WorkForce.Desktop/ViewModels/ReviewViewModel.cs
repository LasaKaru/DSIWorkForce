using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Desktop.Services;

namespace DSI.WorkForce.Desktop.ViewModels;

public sealed partial class FieldRow : ObservableObject
{
    public required WorksheetValueDto Source { get; init; }
    public string Label => Source.Label;
    public string Group => Source.Group ?? "Other";
    public string? Read => Source.RawText;
    public decimal Confidence => Source.Confidence;
    public bool NeedsAttention { get; init; }
    [ObservableProperty] private string? _value;
    public bool IsChanged => !string.Equals(Value ?? "", Source.Value ?? "", StringComparison.Ordinal);
}

public sealed partial class ReviewViewModel(ApiClient api) : ObservableObject
{
    public ObservableCollection<WorksheetSummaryDto> Queue { get; } = new();
    public ObservableCollection<FieldRow> Fields { get; } = new();
    public ObservableCollection<ValidationIssueDto> Issues { get; } = new();

    [ObservableProperty] private string _statusFilter = "NeedsReview";
    [ObservableProperty] private WorksheetSummaryDto? _selectedSheet;
    [ObservableProperty] private FieldRow? _selectedField;
    [ObservableProperty] private BitmapSource? _pageImage;
    [ObservableProperty] private BitmapSource? _snippet;
    [ObservableProperty] private bool _showAllFields;
    [ObservableProperty] private string? _rejectReason;
    [ObservableProperty] private string _status = "";

    private WorksheetDetailDto? _detail;

    public string[] Statuses { get; } = ["NeedsReview", "AutoApproved", "Verified", "Rejected", ""];

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            Queue.Clear();
            foreach (var w in await api.GetWorksheetsAsync(StatusFilter)) Queue.Add(w);
            Status = $"{Queue.Count} sheet(s)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    partial void OnSelectedSheetChanged(WorksheetSummaryDto? value) { if (value is not null) _ = LoadAsync(value.WorksheetId); }
    partial void OnShowAllFieldsChanged(bool value) => FillFields();

    partial void OnSelectedFieldChanged(FieldRow? value)
    {
        Snippet = null;
        if (value?.Source.Zone is not { } z || PageImage is null) return;
        var rect = new Int32Rect((int)(z.X * PageImage.PixelWidth), (int)(z.Y * PageImage.PixelHeight),
            Math.Max(1, (int)(z.W * PageImage.PixelWidth)), Math.Max(1, (int)(z.H * PageImage.PixelHeight)));
        rect.Width = Math.Min(rect.Width, PageImage.PixelWidth - rect.X);
        rect.Height = Math.Min(rect.Height, PageImage.PixelHeight - rect.Y);
        if (rect.Width > 0 && rect.Height > 0) Snippet = new CroppedBitmap(PageImage, rect);
    }

    private async Task LoadAsync(long id)
    {
        try
        {
            _detail = await api.GetWorksheetAsync(id);
            var bytes = await api.GetWorksheetImageAsync(id);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            PageImage = bmp;
            Issues.Clear();
            foreach (var i in _detail.Issues) Issues.Add(i);
            FillFields();
            Status = $"Sheet {id}: {_detail.Issues.Count} open issue(s)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private void FillFields()
    {
        Fields.Clear();
        if (_detail is null) return;
        foreach (var v in _detail.Values.Where(v => !v.IsComputed))   // hourly sums are recalculated by the server
        {
            var attention = v.Confidence < 0.9m || _detail.Issues.Any(i => i.FieldKey == v.FieldKey && i.RowIndex == v.RowIndex);
            if (ShowAllFields || attention) Fields.Add(new FieldRow { Source = v, NeedsAttention = attention, Value = v.Value });
        }
        SelectedField = Fields.FirstOrDefault();
    }

    [RelayCommand] private Task SaveAsync() => SaveCoreAsync(false);
    [RelayCommand] private Task VerifyAndNextAsync() => SaveCoreAsync(true);

    private async Task SaveCoreAsync(bool verify)
    {
        if (_detail is null || SelectedSheet is null) return;
        var changes = Fields.Where(f => f.IsChanged).Select(f => new ValueCorrectionDto(f.Source.FieldKey, f.Source.RowIndex, f.Value)).ToList();
        try
        {
            await api.SaveWorksheetAsync(SelectedSheet.WorksheetId, new SaveCorrectionsRequest(changes, verify, _detail.RowVersion));
            Status = $"Saved {changes.Count} change(s){(verify ? " and verified" : "")}.";
            if (verify) await NextAsync();
            else await LoadAsync(SelectedSheet.WorksheetId);
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (SelectedSheet is null || string.IsNullOrWhiteSpace(RejectReason)) { Status = "Enter a reject reason first."; return; }
        await api.RejectWorksheetAsync(SelectedSheet.WorksheetId, RejectReason);
        RejectReason = null;
        await NextAsync();
    }

    private async Task NextAsync()
    {
        var current = SelectedSheet;
        await RefreshAsync();
        SelectedSheet = Queue.FirstOrDefault(q => q.WorksheetId != current?.WorksheetId);
    }
}
