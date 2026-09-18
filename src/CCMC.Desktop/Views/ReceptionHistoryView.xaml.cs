using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Desktop.Controls;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;

namespace CCMC.Desktop.Views;

/// <summary>
/// Reception History - moved from Windows/ReceptionHistoryWindow (a top-level Window) to a
/// UserControl hosted in MainWindow's single content area (2026-09-18 single-window shell
/// redesign). Same client-side filtering approach as before (load everything cached locally
/// once, filter in memory) - just with a date-period filter added and the search predicate
/// widened to cover every field a row actually displays (see ApplyFilter/HistoryRow).
/// </summary>
public partial class ReceptionHistoryView : UserControl
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly IReceptionRepository _receptionRepository;

    private List<HistoryRow> _allRows = [];
    private bool _initialFilterApplied;

    private static readonly (string Label, HistoryDatePeriod Period)[] DatePeriodOptions =
    [
        ("Today", HistoryDatePeriod.Today),
        ("Past Week", HistoryDatePeriod.PastWeek),
        ("Past Month", HistoryDatePeriod.PastMonth),
        ("Past Year", HistoryDatePeriod.PastYear),
        ("Total", HistoryDatePeriod.Total),
    ];

    private enum HistoryDatePeriod
    {
        Today,
        PastWeek,
        PastMonth,
        PastYear,
        Total,
    }

    /// <summary>
    /// Set by a caller (MainWindow's Dashboard "Accepted"/"Hold"/"Rejected" cards - see
    /// MainWindow.xaml.cs's ShowHistory) BEFORE this control is placed in the content area, so
    /// LoadAsync can apply it once the data and StatusFilterComboBox items exist. Must match one
    /// of the literal option strings in StatusFilterComboBox's ItemsSource below ("Accepted"/
    /// "Hold"/"Rejected"); null or "All statuses" means no filter (the existing default). This
    /// reuses the exact same client-side filtering ApplyFilter() already does for the user's own
    /// manual filter changes - not a second filtering mechanism.
    /// </summary>
    public string? InitialStatusFilter { get; set; }

    public ReceptionHistoryView(
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

        // Default period is "Today" (this redesign's explicit requirement) - index 0 of
        // DatePeriodOptions above, so the default selection and the enum default agree by
        // construction rather than by two separately-maintained constants.
        DatePeriodComboBox.ItemsSource = DatePeriodOptions.Select(o => o.Label).ToList();
        DatePeriodComboBox.SelectedIndex = 0;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var session = _sessionStore.Current;

        // Source/vehicle names are resolved from the same repositories SourcesView/
        // VehiclesView already use (existing data only - never fabricated), across every
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

        // Loads every locally captured reception (not just a fixed recent batch) so the
        // "Total" date-period option genuinely means "all available reception history", per
        // this redesign's explicit requirement - IReceptionRepository.ListRecentAsync already
        // takes an unbounded "take" (a plain SQL LIMIT), so this is a parameter value change,
        // not a new query/endpoint/schema change.
        var transactions = await _receptionRepository.ListRecentAsync(int.MaxValue, CancellationToken.None);
        _allRows = transactions
            .Select((t, i) => HistoryRow.From(t, sourceNames, vehicleNumbers, i))
            .ToList();

        // Apply a Dashboard-supplied initial filter exactly once - a later manual Refresh
        // (RefreshButton_Click, also routed through LoadAsync) must not keep re-forcing it
        // over whatever filter the operator has since chosen themselves.
        if (!_initialFilterApplied && !string.IsNullOrEmpty(InitialStatusFilter))
        {
            _initialFilterApplied = true;
            var items = (string[])StatusFilterComboBox.ItemsSource;
            var match = items.FirstOrDefault(i => string.Equals(i, InitialStatusFilter, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.Equals(StatusFilterComboBox.SelectedItem as string, match, StringComparison.Ordinal))
            {
                StatusFilterComboBox.SelectedItem = match; // triggers Filter_Changed -> ApplyFilter
                return;
            }
        }

        ApplyFilter();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!IsLoaded) return;

        var search = SearchTextBox.Text?.Trim() ?? string.Empty;
        var statusFilter = StatusFilterComboBox.SelectedItem as string ?? "All statuses";
        var periodIndex = DatePeriodComboBox.SelectedIndex;
        var period = periodIndex >= 0 && periodIndex < DatePeriodOptions.Length
            ? DatePeriodOptions[periodIndex].Period
            : HistoryDatePeriod.Today;

        var today = DateTime.Today; // local application date - never hard-coded
        var earliestDateInclusive = period switch
        {
            HistoryDatePeriod.Today => today,
            HistoryDatePeriod.PastWeek => today.AddDays(-6), // 7-day window including today
            HistoryDatePeriod.PastMonth => today.AddDays(-29), // 30-day window including today
            HistoryDatePeriod.PastYear => today.AddDays(-364), // 365-day window including today
            _ => (DateTime?)null, // Total - no lower bound
        };

        var filtered = _allRows.Where(r =>
            (earliestDateInclusive is null || r.CapturedDate >= earliestDateInclusive.Value) &&
            (statusFilter == "All statuses" || string.Equals(r.Status.ToString(), statusFilter, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(search) || r.SearchHaystack.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        RefreshSummary(filtered);

        RowsItemsControl.ItemsSource = filtered;
        RowsItemsControl.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = _allRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesPanel.Visibility = _allRows.Count > 0 && filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// KPI cards reflect the currently filtered set (date period + status + search combined),
    /// not the full unfiltered load - so "Today"'s totals actually mean today's totals, etc.
    /// </summary>
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
        public required DateTime CapturedDate { get; init; }
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

        /// <summary>
        /// Lower-cased, space-joined text of every field this row actually shows (transaction #,
        /// source, vehicle, quantity, fat, SNF, CLR, water, protein, temperature, rate, amount,
        /// reading source, status, sync state, and the captured date/time) - built once here so
        /// ApplyFilter's search predicate is a single Contains call. Numeric fields are included
        /// both in their raw decimal form (e.g. "49.2") and their displayed/rounded form (e.g.
        /// "49.20") so a search for either matches, per this redesign's explicit "search Amount"
        /// requirement. Only fields that actually exist on MilkReceptionTransaction/this row are
        /// included - nothing invented.
        /// </summary>
        public required string SearchHaystack { get; init; }

        public static HistoryRow From(
            CCMC.Domain.Entities.MilkReceptionTransaction t,
            IReadOnlyDictionary<int, string> sourceNames,
            IReadOnlyDictionary<int, string> vehicleNumbers,
            int index)
        {
            var (statusGlyph, statusBorderKey, statusIconKey, statusTextKey) = t.Status switch
            {
                TransactionStatus.Accepted => ("", "SuccessChipBorder", "SuccessChipIcon", "SuccessChipText"),
                TransactionStatus.Hold => ("", "WarningChipBorder", "WarningChipIcon", "WarningChipText"),
                TransactionStatus.Rejected => ("", "DangerChipBorder", "DangerChipIcon", "DangerChipText"),
                _ => ("", "NeutralChipBorder", "NeutralChipIcon", "NeutralChipText"),
            };

            var isDevice = t.ReadingSource == ReadingSource.Device;
            var (readingBorderKey, readingTextKey) = isDevice ? ("InfoChipBorder", "InfoChipText") : ("NeutralChipBorder", "NeutralChipText");

            var qualityParts = new List<string> { $"FAT {t.Fat:F1}", $"SNF {t.Snf:F1}" };
            if (t.Clr is { } clr) qualityParts.Add($"CLR {clr:F1}");

            var sourceName = sourceNames.TryGetValue(t.SourceId, out var s) ? s : $"Source #{t.SourceId}";
            var vehicleNumber = vehicleNumbers.TryGetValue(t.VehicleId, out var v) ? v : $"Vehicle #{t.VehicleId}";
            var transactionLabel = t.TransactionNumber ?? $"Local #{t.LocalId}";
            var capturedText = t.CapturedAt.LocalDateTime.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);
            var quantityText = $"{t.QuantityKg:F1} kg";
            var rateText = t.Rate is { } rate ? $"Rate {rate:F2}" : "Rate —";
            var amount = t.Amount ?? 0m;
            var amountText = t.Amount is { } amount2 ? amount2.ToString("F2", CultureInfo.InvariantCulture) : "—";
            var readingSourceText = t.ReadingSource.ToString().ToUpperInvariant();
            var reasonText = t.Reason ?? string.Empty;
            var statusText = t.Status.ToString().ToUpperInvariant();
            var syncText = t.CloudTransactionId is { } cloudId ? $"Synced (#{cloudId})" : "Pending sync";

            var haystackParts = new List<string>
            {
                transactionLabel, sourceName, vehicleNumber, capturedText, quantityText,
                t.QuantityKg.ToString(CultureInfo.InvariantCulture),
                t.Fat.ToString(CultureInfo.InvariantCulture), $"{t.Fat:F1}",
                t.Snf.ToString(CultureInfo.InvariantCulture), $"{t.Snf:F1}",
                t.Temperature.ToString(CultureInfo.InvariantCulture), $"{t.Temperature:F1}",
                readingSourceText, statusText, syncText, reasonText,
            };
            if (t.Clr is { } clrValue) { haystackParts.Add(clrValue.ToString(CultureInfo.InvariantCulture)); haystackParts.Add($"{clrValue:F1}"); }
            if (t.Water is { } waterValue) { haystackParts.Add(waterValue.ToString(CultureInfo.InvariantCulture)); haystackParts.Add($"{waterValue:F1}"); }
            if (t.Protein is { } proteinValue) { haystackParts.Add(proteinValue.ToString(CultureInfo.InvariantCulture)); haystackParts.Add($"{proteinValue:F1}"); }
            if (t.Rate is { } rateValue) { haystackParts.Add(rateValue.ToString(CultureInfo.InvariantCulture)); haystackParts.Add($"{rateValue:F2}"); }
            if (t.Amount is { } amountValue) { haystackParts.Add(amountValue.ToString(CultureInfo.InvariantCulture)); haystackParts.Add($"{amountValue:F2}"); }

            return new HistoryRow
            {
                SourceName = sourceName,
                VehicleNumber = vehicleNumber,
                TransactionLabel = transactionLabel,
                CapturedText = capturedText,
                CapturedDate = t.CapturedAt.LocalDateTime.Date,
                QuantityKg = t.QuantityKg,
                QuantityText = quantityText,
                QualityText = string.Join(" · ", qualityParts),
                RateText = rateText,
                Amount = amount,
                AmountText = amountText,
                ReadingSourceText = readingSourceText,
                ReadingSourceChipStyle = (Style)System.Windows.Application.Current.FindResource(readingBorderKey),
                ReadingSourceTextStyle = (Style)System.Windows.Application.Current.FindResource(readingTextKey),
                ReasonText = reasonText,
                ReasonVisibility = string.IsNullOrWhiteSpace(t.Reason) ? Visibility.Collapsed : Visibility.Visible,
                Status = t.Status,
                StatusText = statusText,
                StatusGlyph = statusGlyph,
                StatusChipBorderStyle = (Style)System.Windows.Application.Current.FindResource(statusBorderKey),
                StatusChipIconStyle = (Style)System.Windows.Application.Current.FindResource(statusIconKey),
                StatusChipTextStyle = (Style)System.Windows.Application.Current.FindResource(statusTextKey),
                SyncText = syncText,
                IsEven = index % 2 == 1,
                SearchHaystack = string.Join(" ", haystackParts).ToLowerInvariant(),
            };
        }
    }
}
