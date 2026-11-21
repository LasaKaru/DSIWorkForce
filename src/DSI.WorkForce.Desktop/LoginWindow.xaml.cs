using System.Windows;
using DSI.WorkForce.Desktop.Services;

namespace DSI.WorkForce.Desktop;

public partial class LoginWindow : Window
{
    private readonly ApiClient _api;

    public LoginWindow(ApiClient api)
    {
        _api = api;
        InitializeComponent();
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var error = await _api.LoginAsync(UserNameBox.Text.Trim(), PasswordBox.Password);
            if (error is null) { DialogResult = true; return; }
            ShowError(error);
        }
        catch (Exception ex) { ShowError("Cannot reach the DSI WorkForce server: " + ex.Message); }
        finally { SignInButton.IsEnabled = true; }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
