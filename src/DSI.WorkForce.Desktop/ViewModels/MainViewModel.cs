using DSI.WorkForce.Desktop.Services;

namespace DSI.WorkForce.Desktop.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel(ApiClient api, DesktopSettings settings)
    {
        Scan = new ScanStationViewModel(api, settings);
        Review = new ReviewViewModel(api);
        Reports = new ReportViewModel(api);
        SignedInAs = $"{api.User?.FullName} ({api.User?.UserName})";
        CanScan = api.Can("Scan");
        CanReview = api.Can("Review");
        CanReports = api.Can("Reports");
    }

    public string SignedInAs { get; init; } = "";
    public bool CanScan { get; init; }
    public bool CanReview { get; init; }
    public bool CanReports { get; init; }

    public ScanStationViewModel Scan { get; }
    public ReviewViewModel Review { get; }
    public ReportViewModel Reports { get; }
}
