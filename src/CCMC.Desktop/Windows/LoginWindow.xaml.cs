using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Application.MasterData;
using Microsoft.Extensions.DependencyInjection;

namespace CCMC.Desktop.Windows;

public partial class LoginWindow : Window
{
    private readonly AuthenticationService _authenticationService;
    private readonly MasterDataSyncService _masterDataSyncService;
    private readonly ISessionStore _sessionStore;
    private readonly IServiceProvider _serviceProvider;

    public LoginWindow(
        AuthenticationService authenticationService,
        MasterDataSyncService masterDataSyncService,
        ISessionStore sessionStore,
        IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _authenticationService = authenticationService;
        _masterDataSyncService = masterDataSyncService;
        _sessionStore = sessionStore;
        _serviceProvider = serviceProvider;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        SetError(null);
        LoginButton.IsEnabled = false;
        LoginButton.Content = "Signing in…";

        try
        {
            var email = EmailTextBox.Text.Trim();
            var password = PasswordBox.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                SetError("Enter both email and password.");
                return;
            }

            var result = await _authenticationService.LoginAsync(email, password, CancellationToken.None);
            if (!result.Success)
            {
                SetError(result.ErrorMessage ?? "Login failed.");
                return;
            }

            // An offline-authenticated session (see CLAUDE.md "Architecture
            // Decisions" - Offline Operator Login) has no usable access token,
            // so attempting the master-data pull would just be a pointless
            // network call that's guaranteed to fail - skip it and use
            // whatever sources/vehicles/quality-rules were cached from the
            // last successful online session instead.
            if (!result.IsOffline)
            {
                // Best-effort pull of master data - a failure here (e.g. the
                // cloud becomes unreachable mid-pull) must not block the
                // operator from proceeding with whatever was last cached.
                try
                {
                    var token = _sessionStore.Current!.AccessToken;
                    await _masterDataSyncService.PullAsync(token, CancellationToken.None);
                }
                catch
                {
                    // Intentionally swallowed - see comment above.
                }
            }

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
            Close();
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Sign In";
        }
    }

    private void SetError(string? message)
    {
        StatusTextBlock.Text = message ?? string.Empty;
        ErrorBanner.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
