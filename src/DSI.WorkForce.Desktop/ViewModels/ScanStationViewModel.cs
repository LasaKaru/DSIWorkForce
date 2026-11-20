using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSI.WorkForce.Core.Contracts;
using DSI.WorkForce.Desktop.Services;
using Microsoft.Win32;

namespace DSI.WorkForce.Desktop.ViewModels;

/// <summary>
/// "Thin scanner app": the scanner software saves PDFs into a local folder, this screen uploads them
/// (manually or automatically) and moves them to an "uploaded" sub-folder. All OCR runs on the server.
/// </summary>
public sealed partial class ScanStationViewModel : ObservableObject
{
    private readonly ApiClient _api;
    private readonly DesktopSettings _settings;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public ScanStationViewModel(ApiClient api, DesktopSettings settings)
    {
        _api = api;
        _settings = settings;
        WatchFolder = settings.WatchFolder ?? "";
        AutoUpload = settings.AutoUploadWatchedFolder;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public ObservableCollection<UploadResultDto> Results { get; } = new();

    [ObservableProperty] private string _watchFolder = "";
    [ObservableProperty] private bool _autoUpload;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private DashboardDto? _dashboard;

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Scans (*.pdf;*.png;*.jpg;*.tif)|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff" };
        if (dlg.ShowDialog() == true) await UploadAsync(dlg.FileNames, moveAfterUpload: false);
    }

    [RelayCommand]
    private Task UploadWatchFolderNowAsync() => UploadFolderAsync();

    private async Task TickAsync()
    {
        try { Dashboard = await _api.GetDashboardAsync(); }
        catch (Exception ex) { Status = "Server not reachable: " + ex.Message; }
        if (AutoUpload) await UploadFolderAsync();
    }

    private async Task UploadFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(WatchFolder) || !Directory.Exists(WatchFolder)) return;
        var cutoff = DateTime.Now.AddSeconds(-5);   // let the scanner finish writing
        var files = Directory.EnumerateFiles(WatchFolder)
            .Where(f => new[] { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => File.GetLastWriteTime(f) < cutoff)
            .Take(50).ToList();
        if (files.Count > 0) await UploadAsync(files, moveAfterUpload: true);
    }

    private async Task UploadAsync(IReadOnlyList<string> files, bool moveAfterUpload)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Status = $"Uploading {files.Count} file(s)…";
            var results = await _api.UploadAsync(_settings.TemplateCode, files);
            foreach (var r in results) Results.Insert(0, r);
            if (moveAfterUpload)
            {
                var done = Path.Combine(WatchFolder, "uploaded");
                Directory.CreateDirectory(done);
                foreach (var (file, result) in files.Zip(results))
                    if (result.Accepted) File.Move(file, Path.Combine(done, Path.GetFileName(file)), overwrite: true);
            }
            Status = $"Uploaded {results.Count(r => r.Accepted)} of {files.Count} at {DateTime.Now:t}";
        }
        catch (Exception ex) { Status = "Upload failed: " + ex.Message; }
        finally { _busy = false; }
    }
}
