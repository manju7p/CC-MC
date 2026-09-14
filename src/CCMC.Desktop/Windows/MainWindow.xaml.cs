using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Application.Sync;
using CCMC.Desktop.Controls;
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
    private readonly IReceptionRepository _receptionRepository;

    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow(
        IServiceProvider serviceProvider,
        ISessionStore sessionStore,
        IOutboxRepository outboxRepository,
        IDeviceManager deviceManager,
        SyncEngineService syncEngineService,
        AuthenticationService authenticationService,
        ICloudApiClient cloudApiClient,
        IReceptionRepository receptionRepository)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _outboxRepository = outboxRepository;
        _deviceManager = deviceManager;
        _syncEngineService = syncEngineService;
        _authenticationService = authenticationService;
        _cloudApiClient = cloudApiClient;
        _receptionRepository = receptionRepository;

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
        OfflineModeBorder.Visibility = isOffline ? Visibility.Visible : Visibility.Collapsed;
        OfflineModeTextBlock.Text = "OFFLINE MODE (sync paused)";
        ReconnectButton.Visibility = isOffline ? Visibility.Visible : Visibility.Collapsed;

        await RefreshStatusAsync();
        await RefreshDashboardSummaryAsync();
        await RefreshRecentReceptionsAsync();

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
            await RefreshRecentReceptionsAsync();
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
        SyncStatusPanel.Children.Clear();
        SyncStatusPanel.Children.Add(StatusChip.Create(
            pending == 0 ? "Up to date" : $"{pending} pending",
            pending == 0 ? ChipKind.Success : ChipKind.Info));

        DeviceStatusPanel.Children.Clear();
        DeviceStatusPanel.Children.Add(BuildDeviceStatusRow("Weighing scale", _deviceManager.WeighingScale?.State ?? DeviceConnectionState.Disconnected));
        DeviceStatusPanel.Children.Add(BuildDeviceStatusRow("Milk analyser", _deviceManager.MilkAnalyser?.State ?? DeviceConnectionState.Disconnected, isLast: true));
    }

    private static UIElement BuildDeviceStatusRow(string label, DeviceConnectionState state, bool isLast = false)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, isLast ? 0 : 8) };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var kind = state switch
        {
            DeviceConnectionState.Connected => ChipKind.Success,
            DeviceConnectionState.Connecting => ChipKind.Info,
            DeviceConnectionState.Error => ChipKind.Danger,
            _ => ChipKind.Neutral,
        };
        var chip = StatusChip.Create(state.ToString(), kind);
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        return row;
    }

    /// <summary>
    /// Shows the most recent locally captured receptions (any sync state) - purely a
    /// read of the existing IReceptionRepository.ListRecentAsync (the same method
    /// ReceptionHistoryWindow already uses), never fabricated/estimated data. Works
    /// offline (SQLite is local) unlike the cloud-backed summary cards above.
    /// </summary>
    private async Task RefreshRecentReceptionsAsync()
    {
        var recent = await _receptionRepository.ListRecentAsync(5, CancellationToken.None);
        RecentReceptionsPanel.Children.Clear();

        if (recent.Count == 0)
        {
            var empty = new Border { Style = (Style)FindResource("EmptyStateBorder"), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "", Style = (Style)FindResource("EmptyStateIconText") });
            stack.Children.Add(new TextBlock { Text = "No receptions captured yet", Style = (Style)FindResource("EmptyStateTitleText") });
            stack.Children.Add(new TextBlock { Text = "Receptions you capture on the Milk Reception screen will appear here.", Style = (Style)FindResource("EmptyStateBodyText") });
            empty.Child = stack;
            RecentReceptionsPanel.Children.Add(empty);
            return;
        }

        foreach (var transaction in recent)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var idText = new TextBlock
            {
                Text = transaction.TransactionNumber ?? $"Local #{transaction.LocalId}",
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(idText, 0);

            var qtyText = new TextBlock
            {
                Text = $"{transaction.QuantityKg:F1} kg   {transaction.CapturedAt.LocalDateTime:g}",
                Style = (Style)FindResource("CaptionText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
            };
            Grid.SetColumn(qtyText, 1);

            var kind = transaction.Status switch
            {
                TransactionStatus.Accepted => ChipKind.Success,
                TransactionStatus.Hold => ChipKind.Warning,
                TransactionStatus.Rejected => ChipKind.Danger,
                _ => ChipKind.Neutral,
            };
            var chip = StatusChip.Create(transaction.Status.ToString().ToUpperInvariant(), kind);
            Grid.SetColumn(chip, 2);

            row.Children.Add(idText);
            row.Children.Add(qtyText);
            row.Children.Add(chip);
            RecentReceptionsPanel.Children.Add(row);
        }
    }

    private void ReceptionButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<ReceptionWindow>());

    private void HistoryButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<ReceptionHistoryWindow>());

    private void SourcesButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<SourcesWindow>());

    private void VehiclesButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<VehiclesWindow>());

    private void DeviceStatusButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<DeviceStatusWindow>());

    private void DeviceConfigButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<DeviceConfigurationWindow>());

    private void SyncStatusButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<SyncStatusWindow>());

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowOwned(_serviceProvider.GetRequiredService<SettingsWindow>());

    /// <summary>
    /// Every secondary window is owned by this shell (Owner + WindowStartupLocation=
    /// CenterOwner, set in each window's own XAML) so it opens centred over the
    /// dashboard, minimizes/restores with it, and never appears as a detached,
    /// unrelated window - see this redesign's window-hierarchy requirement. Still
    /// non-modal (.Show(), not .ShowDialog()) - unchanged from before, so an operator
    /// can still have Reception and History open side by side if they choose.
    /// </summary>
    private void ShowOwned(Window window)
    {
        window.Owner = this;
        window.Show();
    }

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
