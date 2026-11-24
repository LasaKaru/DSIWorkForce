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
        if (api.CertificateCheckDisabled) CertWarning.Visibility = Visibility.Visible;
        Loaded += async (_, _) => CopyrightText.Text = await _api.GetCopyrightAsync();
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
        catch (System.Net.Http.HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            ShowError("The server's HTTPS certificate is not trusted on this PC. Either run 'dotnet dev-certs https --trust' " +
                      "(development), install the server's certificate, or set \"AllowUntrustedCertificate\": true in desktopsettings.json.");
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
