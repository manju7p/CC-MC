using System.Windows;
using System.Windows.Threading;
using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Application.Sync;
using CCMC.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace CCMC.Desktop.Windows;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISessionStore _sessionStore;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IDeviceManager _deviceManager;
    private readonly SyncEngineService _syncEngineService;
    private readonly AuthenticationService _authenticationService;
    private readonly ICloudApiClient _cloudApiClient;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow(
        IServiceProvider serviceProvider,
        ISessionStore sessionStore,
        IOutboxRepository outboxRepository,
        IDeviceManager deviceManager,
        SyncEngineService syncEngineService,
        AuthenticationService authenticationService,
        ICloudApiClient cloudApiClient)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _outboxRepository = outboxRepository;
        _deviceManager = deviceManager;
        _syncEngineService = syncEngineService;
        _authenticationService = authenticationService;
        _cloudApiClient = cloudApiClient;

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _syncTimer.Stop();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var session = _sessionStore.Current;
        WelcomeTextBlock.Text = session is not null
            ? $"{session.User.FullName} ({string.Join(", ", session.User.Roles)})"
            : string.Empty;

        // Offline-authenticated session (see CLAUDE.md "Architecture Decisions" -
        // Offline Operator Login): no valid access token, so sync stays paused
        // (SyncEngineService treats IsOffline the same as "no session") until the
        // operator explicitly signs in online again via ReconnectButton.
        var isOffline = session?.IsOffline ?? false;
        OfflineModeTextBlock.Visibility = isOffline ? Visibility.Visible : Visibility.Collapsed;
        OfflineModeTextBlock.Text = "OFFLINE MODE (sync paused)";
        ReconnectButton.Visibility = isOffline ? Visibility.Visible : Visibility.Collapsed;

        await RefreshStatusAsync();
        await RefreshDashboardSummaryAsync();

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();

        _syncTimer.Tick += async (_, _) => await RunSyncTickAsync();
        _syncTimer.Start();

        // Run one tick immediately at startup too, after the outbox's stale-processing sweep.
        await _syncEngineService.RecoverAtStartupAsync(CancellationToken.None);
        await RunSyncTickAsync();
    }

    private async Task RunSyncTickAsync()
    {
        try
        {
            await _syncEngineService.TickAsync(CancellationToken.None);
        }
        catch
        {
            // A sync tick failing must never take down the app - the next
            // scheduled tick will simply try again.
        }
        finally
        {
            await RefreshStatusAsync();
            await RefreshDashboardSummaryAsync(); // a reception may have just synced - reflect it in the cards
        }
    }

    /// <summary>
    /// Populates the dashboard summary cards from the cloud's existing
    /// GET /dashboard/summary endpoint (ICloudApiClient.GetDashboardSummaryAsync -
    /// already implemented, just never called from any WPF window before this).
    /// Requires an online session; an offline/missing session or any request
    /// failure (network, permission, etc.) shows an explanatory caption
    /// instead of fabricating figures - never invents dashboard data.
    /// </summary>
    private async Task RefreshDashboardSummaryAsync()
    {
        var session = _sessionStore.Current;
        if (session is null || session.IsOffline)
        {
            SetDashboardUnavailable(session is null ? "Sign in to see today's summary." : "Dashboard summary requires an online session.");
            return;
        }

        try
        {
            var summary = await _cloudApiClient.GetDashboardSummaryAsync(session.AccessToken, centreId: null, CancellationToken.None);
            TodayTotalTextBlock.Text = summary.TotalTransactions.ToString();
            TodayAcceptedTextBlock.Text = summary.Accepted.ToString();
            TodayHoldTextBlock.Text = summary.Hold.ToString();
            TodayRejectedTextBlock.Text = summary.Rejected.ToString();
            DashboardUnavailableTextBlock.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Cloud unreachable, permission denied, etc. - show a placeholder, never a crash and never invented numbers.
            SetDashboardUnavailable("Today's summary is unavailable right now (cloud unreachable or not permitted).");
        }
    }

    private void SetDashboardUnavailable(string message)
    {
        TodayTotalTextBlock.Text = "—";
        TodayAcceptedTextBlock.Text = "—";
        TodayHoldTextBlock.Text = "—";
        TodayRejectedTextBlock.Text = "—";
        DashboardUnavailableTextBlock.Text = message;
        DashboardUnavailableTextBlock.Visibility = Visibility.Visible;
    }

    private async Task RefreshStatusAsync()
    {
        var pending = await _outboxRepository.CountPendingAsync(CancellationToken.None);
        SyncStatusTextBlock.Text = pending == 0 ? "Sync: up to date" : $"Sync: {pending} pending";

        var scaleState = _deviceManager.WeighingScale?.State ?? DeviceConnectionState.Disconnected;
        var analyserState = _deviceManager.MilkAnalyser?.State ?? DeviceConnectionState.Disconnected;
        ConnectivityStatusTextBlock.Text = $"Scale: {scaleState}   |   Analyser: {analyserState}";
    }

    private void ReceptionButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<ReceptionWindow>().Show();

    private void HistoryButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<ReceptionHistoryWindow>().Show();

    private void SourcesButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<SourcesWindow>().Show();

    private void VehiclesButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<VehiclesWindow>().Show();

    private void DeviceStatusButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<DeviceStatusWindow>().Show();

    private void DeviceConfigButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<DeviceConfigurationWindow>().Show();

    private void SyncStatusButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<SyncStatusWindow>().Show();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        _serviceProvider.GetRequiredService<SettingsWindow>().Show();

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _authenticationService.LogoutAsync(CancellationToken.None);
        var login = _serviceProvider.GetRequiredService<LoginWindow>();
        login.Show();
        Close();
    }

    /// <summary>
    /// "WHEN ONLINE AGAIN" (CLAUDE.md "Architecture Decisions" - Offline
    /// Operator Login): signs the operator out of the offline session and
    /// reopens LoginWindow so they can authenticate online with connectivity
    /// restored - a real online login always refreshes identity/role/centre
    /// access from the API and resumes synchronization, satisfying "cloud
    /// remains authoritative" without a separate silent-background-reauth
    /// mechanism (deliberately not built - see that decision's "do not
    /// over-engineer" instruction).
    /// </summary>
    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await _authenticationService.LogoutAsync(CancellationToken.None);
        var login = _serviceProvider.GetRequiredService<LoginWindow>();
        login.Show();
        Close();
    }
}
