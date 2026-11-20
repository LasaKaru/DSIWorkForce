using System.Windows;
using System.Windows.Threading;
using DSI.WorkForce.Desktop.Services;
using DSI.WorkForce.Desktop.ViewModels;

namespace DSI.WorkForce.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var settings = DesktopSettings.Load();
        var api = new ApiClient(settings);
        var window = new MainWindow { DataContext = new MainViewModel(api, settings) };
        window.Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the scan station running; show the error instead of crashing.
        MessageBox.Show(e.Exception.Message, "DSI WorkForce", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
