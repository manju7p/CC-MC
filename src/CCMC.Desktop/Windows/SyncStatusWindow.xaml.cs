using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Application.Sync;

namespace CCMC.Desktop.Windows;

public partial class SyncStatusWindow : Window
{
    private readonly IOutboxRepository _outboxRepository;
    private readonly IOverrideOutboxRepository _overrideOutboxRepository;
    private readonly SyncEngineService _syncEngineService;
    private readonly ISessionStore _sessionStore;

    public SyncStatusWindow(
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
        PendingTextBlock.Text = $"Pending receptions: {pending}    |    Pending overrides: {overridesPending}";

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
