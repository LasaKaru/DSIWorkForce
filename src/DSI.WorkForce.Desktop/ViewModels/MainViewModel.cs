using DSI.WorkForce.Desktop.Services;

namespace DSI.WorkForce.Desktop.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel(ApiClient api, DesktopSettings settings)
    {
        Scan = new ScanStationViewModel(api, settings);
        Review = new ReviewViewModel(api);
        Reports = new ReportViewModel(api);
    }

    public ScanStationViewModel Scan { get; }
    public ReviewViewModel Review { get; }
    public ReportViewModel Reports { get; }
}
