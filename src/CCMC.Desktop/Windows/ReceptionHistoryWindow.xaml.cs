using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Desktop.Controls;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;

namespace CCMC.Desktop.Windows;

public partial class ReceptionHistoryWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly IReceptionRepository _receptionRepository;

    private List<HistoryRow> _allRows = [];

    public ReceptionHistoryWindow(
        ISessionStore sessionStore,
        IChillingCentreRepository centreRepository,
        ISourceRepository sourceRepository,
        IVehicleRepository vehicleRepository,
        IReceptionRepository receptionRepository)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _sourceRepository = sourceRepository;
        _vehicleRepository = vehicleRepository;
        _receptionRepository = receptionRepository;

        StatusFilterComboBox.ItemsSource = new[] { "All statuses", "Accepted", "Hold", "Rejected" };
        StatusFilterComboBox.SelectedIndex = 0;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var session = _sessionStore.Current;

        // Source/vehicle names are resolved from the same repositories SourcesWindow/
        // VehiclesWindow already use (existing data only - never fabricated), across every
        // centre this operator can see, so a row never shows a raw numeric id to the user.
        var sourceNames = new Dictionary<int, string>();
        var vehicleNumbers = new Dictionary<int, string>();
        if (session is not null)
        {
            var centres = await _centreRepository.ListAsync(CancellationToken.None);
            var accessible = session.User.CentreAccess.AllCentres
                ? centres
                : centres.Where(c => session.User.CentreAccess.CentreIds.Contains(c.Id)).ToList();

            foreach (var centre in accessible)
            {
                foreach (var source in await _sourceRepository.ListByCentreAsync(centre.Id, CancellationToken.None))
                {
                    sourceNames[source.Id] = source.Name;
                }
                foreach (var vehicle in await _vehicleRepository.ListByCentreAsync(centre.Id, CancellationToken.None))
                {
                    vehicleNumbers[vehicle.Id] = vehicle.VehicleNumber;
                }
            }
        }

        var transactions = await _receptionRepository.ListRecentAsync(200, CancellationToken.None);
        _allRows = transactions
            .Select((t, i) => HistoryRow.From(t, sourceNames, vehicleNumbers, i))
            .ToList();

        RefreshSummary(_allRows);
        ApplyFilter();
    }

    private void RefreshSummary(IReadOnlyCollection<HistoryRow> rows)
    {
        TotalCountTextBlock.Text = rows.Count.ToString();
        TotalQuantityTextBlock.Text = rows.Sum(r => r.QuantityKg).ToString("N1", CultureInfo.InvariantCulture);
        TotalAmountTextBlock.Text = rows.Sum(r => r.Amount).ToString("N2", CultureInfo.InvariantCulture);

        var accepted = rows.Count(r => r.Status == TransactionStatus.Accepted);
        var hold = rows.Count(r => r.Status == TransactionStatus.Hold);
        var rejected = rows.Count(r => r.Status == TransactionStatus.Rejected);
        StatusBreakdownTextBlock.Text = $"{accepted} / {hold} / {rejected}";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsLoaded) return;

        var search = SearchTextBox.Text?.Trim() ?? string.Empty;
        var statusFilter = StatusFilterComboBox.SelectedItem as string ?? "All statuses";

        var filtered = _allRows.Where(r =>
            (statusFilter == "All statuses" || string.Equals(r.Status.ToString(), statusFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(search) ||
                r.SourceName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.VehicleNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                r.TransactionLabel.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        RowsItemsControl.ItemsSource = filtered;
        RowsItemsControl.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesPanel.Visibility = _allRows.Count > 0 && filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Display-shaping only - formats an existing <see cref="CCMC.Domain.Entities.MilkReceptionTransaction"/>
    /// for the card template above (HistoryRowTemplate). Computes no business/quality rules of
    /// its own (CLAUDE.md "business logic stays out of the UI layer") - every value here is either
    /// copied verbatim or purely cosmetically formatted (rounding for display, not recalculated).
    /// </summary>
    private sealed class HistoryRow
    {
        public required string SourceName { get; init; }
        public required string VehicleNumber { get; init; }
        public required string TransactionLabel { get; init; }
        public required string CapturedText { get; init; }
        public required decimal QuantityKg { get; init; }
        public required string QuantityText { get; init; }
        public required string QualityText { get; init; }
        public required string RateText { get; init; }
        public required decimal Amount { get; init; }
        public required string AmountText { get; init; }
        public required string ReadingSourceText { get; init; }
        public required Style ReadingSourceChipStyle { get; init; }
        public required Style ReadingSourceTextStyle { get; init; }
        public required string ReasonText { get; init; }
        public required Visibility ReasonVisibility { get; init; }
        public required TransactionStatus Status { get; init; }
        public required string StatusText { get; init; }
        public required string StatusGlyph { get; init; }
        public required Style StatusChipBorderStyle { get; init; }
        public required Style StatusChipIconStyle { get; init; }
        public required Style StatusChipTextStyle { get; init; }
        public required string SyncText { get; init; }
        public required bool IsEven { get; init; }

        public static HistoryRow From(
            CCMC.Domain.Entities.MilkReceptionTransaction t,
            IReadOnlyDictionary<int, string> sourceNames,
            IReadOnlyDictionary<int, string> vehicleNumbers,
            int index)
        {
            var (statusGlyph, statusBorderKey, statusIconKey, statusTextKey) = t.Status switch
            {
                TransactionStatus.Accepted => ("", "SuccessChipBorder", "SuccessChipIcon", "SuccessChipText"),
                TransactionStatus.Hold => ("", "WarningChipBorder", "WarningChipIcon", "WarningChipText"),
                TransactionStatus.Rejected => ("", "DangerChipBorder", "DangerChipIcon", "DangerChipText"),
                _ => ("", "NeutralChipBorder", "NeutralChipIcon", "NeutralChipText"),
            };

            var isDevice = t.ReadingSource == ReadingSource.Device;
            var (readingBorderKey, readingTextKey) = isDevice ? ("InfoChipBorder", "InfoChipText") : ("NeutralChipBorder", "NeutralChipText");

            var qualityParts = new List<string> { $"FAT {t.Fat:F1}", $"SNF {t.Snf:F1}" };
            if (t.Clr is { } clr) qualityParts.Add($"CLR {clr:F1}");

            return new HistoryRow
            {
                SourceName = sourceNames.TryGetValue(t.SourceId, out var s) ? s : $"Source #{t.SourceId}",
                VehicleNumber = vehicleNumbers.TryGetValue(t.VehicleId, out var v) ? v : $"Vehicle #{t.VehicleId}",
                TransactionLabel = t.TransactionNumber ?? $"Local #{t.LocalId}",
                CapturedText = t.CapturedAt.LocalDateTime.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture),
                QuantityKg = t.QuantityKg,
                QuantityText = $"{t.QuantityKg:F1} kg",
                QualityText = string.Join(" · ", qualityParts),
                RateText = t.Rate is { } rate ? $"Rate {rate:F2}" : "Rate —",
                Amount = t.Amount ?? 0m,
                AmountText = t.Amount is { } amount ? amount.ToString("F2", CultureInfo.InvariantCulture) : "—",
                ReadingSourceText = t.ReadingSource.ToString().ToUpperInvariant(),
                ReadingSourceChipStyle = (Style)System.Windows.Application.Current.FindResource(readingBorderKey),
                ReadingSourceTextStyle = (Style)System.Windows.Application.Current.FindResource(readingTextKey),
                ReasonText = t.Reason ?? string.Empty,
                ReasonVisibility = string.IsNullOrWhiteSpace(t.Reason) ? Visibility.Collapsed : Visibility.Visible,
                Status = t.Status,
                StatusText = t.Status.ToString().ToUpperInvariant(),
                StatusGlyph = statusGlyph,
                StatusChipBorderStyle = (Style)System.Windows.Application.Current.FindResource(statusBorderKey),
                StatusChipIconStyle = (Style)System.Windows.Application.Current.FindResource(statusIconKey),
                StatusChipTextStyle = (Style)System.Windows.Application.Current.FindResource(statusTextKey),
                SyncText = t.CloudTransactionId is { } cloudId ? $"Synced (#{cloudId})" : "Pending sync",
                IsEven = index % 2 == 1,
            };
        }
    }
}
