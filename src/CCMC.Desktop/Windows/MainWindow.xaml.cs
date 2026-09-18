using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Application.Sync;
using CCMC.Desktop.Controls;
using CCMC.Desktop.Views;
using CCMC.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace CCMC.Desktop.Windows;

/// <summary>
/// The single-window application shell (2026-09-18 redesign - see CLAUDE.md "single-window
/// application shell" requirement). Left sidebar navigation + a right ContentControl
/// (MainContent) that swaps in the selected page - Dashboard/Reception/History/Sources/
/// Vehicles/Synchronization/Settings all render here now instead of each opening its own
/// top-level Window. Dashboard's own markup/logic stays exactly where it was (declared inline
/// in MainWindow.xaml, driven by this class) - it is simply the ContentControl's initial
/// Content instead of the whole window's only content; every other page is a UserControl
/// built via DI (see Views/*) and assigned to MainContent.Content on demand.
/// </summary>
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

    /// <summary>Captured once at Loaded (the Dashboard content declared inline in XAML), so ShowDashboard() can restore it after another page has replaced MainContent.Content.</summary>
    private object? _dashboardContent;

    private readonly List<Button> _navButtons = [];

    /// <summary>
    /// Sources/Vehicles nav visibility (CLAUDE.md RBAC requirement: hidden for Operator,
    /// visible for Manager/Admin). Deliberately role-based, not permission-based - the
    /// Operator role is actually granted SOURCE_VIEW/VEHICLE_VIEW server-side (BRD v2 section
    /// 14's least-privilege grant, so Reception's own source/vehicle pickers keep working for
    /// an Operator) - so gating on that permission would NOT hide these nav items for an
    /// Operator. This mirrors the explicit nav requirement instead, and is UX-only, same as
    /// every other client-side check in this app: the server's own [RequirePermission] guards
    /// remain the real authorization boundary regardless of what this shell shows or hides.
    /// </summary>
    private bool _canManageSourcesAndVehicles;

    /// <summary>
    /// Rate Configuration nav visibility - Manager/Admin only, hidden for Operator (this
    /// correction pass's explicit requirement). Computed the same way as
    /// <see cref="_canManageSourcesAndVehicles"/> for the same reason (role-based nav
    /// visibility, not permission-based) - kept as a separate flag rather than reusing that
    /// one because the two capabilities are conceptually distinct even though today's role
    /// grants happen to produce the same Manager/Admin result. The SAVE action inside
    /// RateConfigurationView itself additionally checks the real RATE_FORMULA_CONFIGURE
    /// permission (defensive, UX-only - see that view's own doc comment); the server's
    /// [RequirePermission] + CentreAccessGuard on PUT /rate-formula-settings remains the
    /// actual authorization boundary regardless of what this flag hides or shows.
    /// </summary>
    private bool _canManageRateConfiguration;

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

        _navButtons.AddRange([NavDashboardButton, NavReceptionButton, NavHistoryButton, NavSourcesButton, NavVehiclesButton, NavRateConfigButton, NavSyncButton, NavSettingsButton]);

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _syncTimer.Stop();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _dashboardContent = MainContent.Content;

        var session = _sessionStore.Current;
        WelcomeTextBlock.Text = session is not null
            ? $"{session.User.FullName} ({string.Join(", ", session.User.Roles)})"
            : string.Empty;

        ApplyRoleBasedNavigation(session);

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

    /// <summary>
    /// Sources/Vehicles are hidden entirely (not just disabled) for an Operator - see
    /// <see cref="_canManageSourcesAndVehicles"/>'s doc comment for why this checks roles
    /// rather than the SOURCE_VIEW/VEHICLE_VIEW permissions. Admin is granted the same
    /// Source/VehicleCreate/Edit permissions as Manager (SeedHelpers.RolePermissions), so
    /// Admin naturally passes this check too - no separate Admin-specific branch needed.
    /// </summary>
    private void ApplyRoleBasedNavigation(Session? session)
    {
        var roles = session?.User.Roles ?? [];
        _canManageSourcesAndVehicles = roles.Any(r =>
            string.Equals(r, "Manager", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));

        NavSourcesButton.Visibility = _canManageSourcesAndVehicles ? Visibility.Visible : Visibility.Collapsed;
        NavVehiclesButton.Visibility = _canManageSourcesAndVehicles ? Visibility.Visible : Visibility.Collapsed;

        _canManageRateConfiguration = _canManageSourcesAndVehicles; // same Manager/Admin role set today - see _canManageRateConfiguration's doc comment
        NavRateConfigButton.Visibility = _canManageRateConfiguration ? Visibility.Visible : Visibility.Collapsed;
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
    /// ReceptionHistoryView already uses), never fabricated/estimated data. Works
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
            stack.Children.Add(new TextBlock { Text = "", Style = (Style)FindResource("EmptyStateIconText") });
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

    // ===================== Single-window navigation =====================
    // Every nav button below swaps MainContent.Content instead of opening a Window - this
    // is the entire "single main window + left nav + right content" mechanism. Sources/
    // Vehicles are additionally guarded here (not just by Visibility.Collapsed on the
    // button) so an Operator cannot reach them if some future code path ever calls these
    // methods directly.

    private void SetActiveNav(Button active)
    {
        foreach (var button in _navButtons)
        {
            button.Style = (Style)FindResource("SidebarButtonStyle");
        }
        active.Style = (Style)FindResource("SidebarNavButtonActiveStyle");
    }

    private void ShowDashboard()
    {
        SetActiveNav(NavDashboardButton);
        MainContent.Content = _dashboardContent;
    }

    private void ShowReception()
    {
        SetActiveNav(NavReceptionButton);
        MainContent.Content = _serviceProvider.GetRequiredService<ReceptionView>();
    }

    private void ShowHistory(string? statusFilter)
    {
        SetActiveNav(NavHistoryButton);
        var view = _serviceProvider.GetRequiredService<ReceptionHistoryView>();
        view.InitialStatusFilter = statusFilter;
        MainContent.Content = view;
    }

    private void ShowSources()
    {
        if (!_canManageSourcesAndVehicles) return;
        SetActiveNav(NavSourcesButton);
        MainContent.Content = _serviceProvider.GetRequiredService<SourcesView>();
    }

    private void ShowVehicles()
    {
        if (!_canManageSourcesAndVehicles) return;
        SetActiveNav(NavVehiclesButton);
        MainContent.Content = _serviceProvider.GetRequiredService<VehiclesView>();
    }

    private void ShowSync()
    {
        SetActiveNav(NavSyncButton);
        MainContent.Content = _serviceProvider.GetRequiredService<SyncStatusView>();
    }

    private void ShowRateConfiguration()
    {
        if (!_canManageRateConfiguration) return;
        SetActiveNav(NavRateConfigButton);
        MainContent.Content = _serviceProvider.GetRequiredService<RateConfigurationView>();
    }

    private void ShowSettings()
    {
        SetActiveNav(NavSettingsButton);
        MainContent.Content = _serviceProvider.GetRequiredService<SettingsView>();
    }

    private void DashboardButton_Click(object sender, RoutedEventArgs e) => ShowDashboard();

    private void ReceptionButton_Click(object sender, RoutedEventArgs e) => ShowReception();

    private void HistoryButton_Click(object sender, RoutedEventArgs e) => ShowHistory(null);

    private void SourcesButton_Click(object sender, RoutedEventArgs e) => ShowSources();

    private void VehiclesButton_Click(object sender, RoutedEventArgs e) => ShowVehicles();

    private void RateConfigButton_Click(object sender, RoutedEventArgs e) => ShowRateConfiguration();

    private void SyncStatusButton_Click(object sender, RoutedEventArgs e) => ShowSync();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>
    /// Opens Reception History pre-filtered to a status - the Dashboard's Receptions/
    /// Accepted/Hold/Rejected cards (see MainWindow.xaml) all route through ShowHistory
    /// instead of building separate filtered screens. <paramref name="statusFilter"/> is one
    /// of ReceptionHistoryView's own filter option strings ("Accepted"/"Hold"/"Rejected"), or
    /// null for "Receptions" (all statuses, no filter).
    /// </summary>
    private void ReceptionsCard_Click(object sender, RoutedEventArgs e) => ShowHistory(null);

    private void AcceptedCard_Click(object sender, RoutedEventArgs e) => ShowHistory("Accepted");

    private void HoldCard_Click(object sender, RoutedEventArgs e) => ShowHistory("Hold");

    private void RejectedCard_Click(object sender, RoutedEventArgs e) => ShowHistory("Rejected");

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
