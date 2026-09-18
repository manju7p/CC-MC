using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Application.Sync;
using CCMC.Desktop.Controls;

namespace CCMC.Desktop.Views;

/// <summary>
/// Synchronization Status - moved from Windows/SyncStatusWindow (a top-level Window) to a
/// UserControl hosted in MainWindow's single content area (2026-09-18 single-window shell
/// redesign). Logic unchanged from SyncStatusWindow.
/// </summary>
public partial class SyncStatusView : UserControl
{
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOverrideOutboxRepository _overrideOutboxRepository;
    private readonly SyncEngineService _syncEngineService;
    private readonly ISessionStore _sessionStore;

    public SyncStatusView(
        IOutboxRepository outboxRepository,
        IOverrideOutboxRepository overrideOutboxRepository,
        SyncEngineService syncEngineService,
        ISessionStore sessionStore)
    {
        InitializeComponent();
        _outboxRepository = outboxRepository;
        _overrideOutboxRepository = overrideOutboxRepository;
        _syncEngineService = syncEngineService;
        _sessionStore = sessionStore;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var pending = await _outboxRepository.CountPendingAsync(CancellationToken.None);
        var overridesPending = await _overrideOutboxRepository.CountPendingAsync(CancellationToken.None);

        PendingHost.Children.Clear();
        PendingHost.Children.Add(StatusChip.Create(
            pending == 0 ? "Receptions up to date" : $"{pending} reception(s) pending",
            pending == 0 ? ChipKind.Success : ChipKind.Info));
        var overrideChip = StatusChip.Create(
            overridesPending == 0 ? "Overrides up to date" : $"{overridesPending} override(s) pending",
            overridesPending == 0 ? ChipKind.Success : ChipKind.Info);
        overrideChip.Margin = new Thickness(8, 0, 0, 0);
        PendingHost.Children.Add(overrideChip);

        var session = _sessionStore.Current;
        NoSessionHintTextBlock.Text = session switch
        {
            null => "No active session - synchronization requires a signed-in operator.",
            { IsOffline: true } => "Signed in OFFLINE - synchronization is paused until you sign in online again (see Dashboard).",
            _ => string.Empty,
        };
    }

    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        try
        {
            var result = await _syncEngineService.TickAsync(CancellationToken.None);
            LastTickTextBlock.Text = result.Skipped > 0
                ? "Skipped - no active online session."
                : $"Receptions - attempted: {result.Attempted}, synced: {result.Synced}, retried: {result.Retried}, failed: {result.Failed}\n" +
                  $"Overrides - attempted: {result.OverridesAttempted}, synced: {result.OverridesSynced}, " +
                  $"retried: {result.OverridesRetried}, failed: {result.OverridesFailed}";
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
            await RefreshAsync();
        }
    }
}
