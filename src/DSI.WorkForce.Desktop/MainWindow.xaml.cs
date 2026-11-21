using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DSI.WorkForce.Desktop.ViewModels;

namespace DSI.WorkForce.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.CanReports) await vm.Reports.LoadReportsCommand.ExecuteAsync(null);
        if (vm.CanReview) await vm.Review.RefreshCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Report columns are dynamic (they come from the SQL view), so bind each generated column through the
    /// DataRowView indexer (works with headers such as "No of Units produced") and apply the report's number format.
    /// </summary>
    private void OnReportColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var name = e.PropertyName;
        var binding = new Binding($"[{name}]");
        if (vm.Reports.Formats.TryGetValue(name, out var fmt) && !string.IsNullOrEmpty(fmt))
            binding.StringFormat = "{0:" + fmt + "}";
        e.Column = new DataGridTextColumn { Header = name, Binding = binding };
    }
}
