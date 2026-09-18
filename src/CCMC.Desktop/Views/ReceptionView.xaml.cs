using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Application.Reception;
using CCMC.Domain.Devices;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Devices;

namespace CCMC.Desktop.Views;

/// <summary>
/// Milk Reception - moved from Windows/ReceptionWindow (a top-level Window) to a UserControl
/// hosted in MainWindow's single content area (2026-09-18 single-window shell redesign). All
/// reception/device/rate logic below is unchanged from ReceptionWindow - only the base class
/// and the lack of an Owner/Show() call are different (see CLAUDE.md "single-window shell").
/// </summary>
public partial class ReceptionView : UserControl
{
    private const string ManualAnalyserDeviceId = "milk-analyser-manual-test";

    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly ReceptionWorkflowService _receptionWorkflowService;

    private readonly ReadingProvenanceTracker _provenance = new();

    /// <summary>
    /// Guards the TextChanged handlers below against firing when THIS class
    /// programmatically sets a field's .Text after a device read (or a
    /// manual analyser test parse) - only a genuine operator keystroke
    /// should ever mark a reading as manually edited. See
    /// ReadDevicesButton_Click/ParseManualAnalyserButton_Click, which set
    /// this around their own .Text assignments.
    /// </summary>
    private bool _suppressProvenanceTracking;

    public ReceptionView(
        ISessionStore sessionStore,
        IChillingCentreRepository centreRepository,
        ISourceRepository sourceRepository,
        IVehicleRepository vehicleRepository,
        ReceptionWorkflowService receptionWorkflowService)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _sourceRepository = sourceRepository;
        _vehicleRepository = vehicleRepository;
        _receptionWorkflowService = receptionWorkflowService;

        Loaded += ReceptionView_Loaded;

        QuantityTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkWeightEditedManually(); };
        FatTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        SnfTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        ClrTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        WaterTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        ProteinTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        TemperatureTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };

        // BRD v5.0 section 25: "Rate is recalculated live as soon as FAT, SNF,
        // and Weight are entered." Fires on both a genuine keystroke and a
        // programmatic .Text assignment (device read / manual analyser test
        // parse both set .Text directly) - either way the inputs changed and
        // the preview must reflect it. Uses ReceptionWorkflowService.CalculateRateAsync -
        // the exact same resolution+calculation path used at save time - so
        // this preview can never diverge from what actually gets persisted.
        QuantityTextBox.TextChanged += async (_, _) => await RecalculateRatePreviewAsync();
        FatTextBox.TextChanged += async (_, _) => await RecalculateRatePreviewAsync();
        SnfTextBox.TextChanged += async (_, _) => await RecalculateRatePreviewAsync();
    }

    /// <summary>
    /// Re-resolves rate formula settings from the local cache on every call
    /// (not cached in this window) so the preview always reflects the same
    /// freshest locally-cached configuration ValidateAndSaveAsync will read
    /// at the moment of ACCEPT/HOLD - see this method's callers.
    /// </summary>
    private async Task RecalculateRatePreviewAsync()
    {
        if (CentreComboBox.SelectedItem is not ChillingCentre centre)
        {
            RateValueTextBlock.Text = "0.00";
            AmountValueTextBlock.Text = "0.00";
            return;
        }

        var fat = TryParseDecimal(FatTextBox.Text, out var fatValue) ? fatValue : (decimal?)null;
        var snf = TryParseDecimal(SnfTextBox.Text, out var snfValue) ? snfValue : (decimal?)null;
        var weight = TryParseDecimal(QuantityTextBox.Text, out var weightValue) ? weightValue : (decimal?)null;

        var result = await _receptionWorkflowService.CalculateRateAsync(centre.Id, fat, snf, weight, CancellationToken.None);
        RateValueTextBlock.Text = result.Rate.ToString("F2", CultureInfo.InvariantCulture);
        AmountValueTextBlock.Text = result.Amount.ToString("F2", CultureInfo.InvariantCulture);
    }

    private async void ReceptionView_Loaded(object sender, RoutedEventArgs e)
    {
        var session = _sessionStore.Current;
        if (session is null)
        {
            ResultTextBlock.Text = "No active session.";
            return;
        }

        var allCentres = await _centreRepository.ListAsync(CancellationToken.None);
        var accessible = session.User.CentreAccess.AllCentres
            ? allCentres
            : allCentres.Where(c => session.User.CentreAccess.CentreIds.Contains(c.Id)).ToList();

        CentreComboBox.ItemsSource = accessible;
        if (accessible.Count > 0)
        {
            CentreComboBox.SelectedIndex = 0;
        }
    }

    private async void CentreComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CentreComboBox.SelectedItem is not ChillingCentre centre) return;

        SourceComboBox.ItemsSource = await _sourceRepository.ListByCentreAsync(centre.Id, CancellationToken.None);
        VehicleComboBox.ItemsSource = await _vehicleRepository.ListByCentreAsync(centre.Id, CancellationToken.None);
        await RecalculateRatePreviewAsync(); // rate formula settings are centre-scoped - a centre change can change the result
    }

    private async void ReadDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        ReadDevicesButton.IsEnabled = false;
        WeightStatusTextBlock.Text = string.Empty;
        QualityStatusTextBlock.Text = string.Empty;
        _provenance.Reset(); // fresh capture cycle - any prior edit state no longer applies

        try
        {
            var result = await _receptionWorkflowService.ReadDevicesAsync(CancellationToken.None);

            _suppressProvenanceTracking = true;
            try
            {
                if (result.Weight is { } weight)
                {
                    QuantityTextBox.Text = weight.Value.ToString(CultureInfo.InvariantCulture);
                    _provenance.RecordWeightFromDevice();
                    WeightStatusTextBlock.Text = weight.Stable ? "Weight read from scale (stable)." : "Weight read from scale.";
                    WeightStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    WeightStatusTextBlock.Text = $"Scale unavailable - enter weight manually. ({result.WeightUnavailableReason})";
                    WeightStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }

                if (result.Quality is { } quality)
                {
                    PopulateQualityFields(quality);
                    _provenance.RecordQualityFromDevice();
                    QualityStatusTextBlock.Text = quality.Temperature is null
                        ? "Quality read from analyser (Fat/SNF/CLR/Water/Protein). Temperature is not measured by this device - enter it manually."
                        : "Quality read from analyser.";
                    QualityStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    QualityStatusTextBlock.Text = $"Analyser unavailable - enter quality manually, or use the Manual Test Input below. ({result.QualityUnavailableReason})";
                    QualityStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;
                }
            }
            finally
            {
                _suppressProvenanceTracking = false;
            }
        }
        finally
        {
            ReadDevicesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Development/testing fallback while the physical KAM98-2A is
    /// unavailable (see CLAUDE.md / HOW_TO_RUN.md "Milk Analyser Manual Test
    /// Input"). Feeds the operator-entered raw string through the EXACT SAME
    /// Kam98A2AAnalyserFrameParser a real device read uses, then populates
    /// the exact same fields ReadDevicesButton_Click does - this is
    /// deliberately not a second, parallel decode path.
    /// </summary>
    private void ParseManualAnalyserButton_Click(object sender, RoutedEventArgs e)
    {
        var input = ManualAnalyserInputTextBox.Text;
        try
        {
            var reading = Kam98A2AAnalyserFrameParser.ParsePayload(input, ManualAnalyserDeviceId, DateTimeOffset.UtcNow);

            _suppressProvenanceTracking = true;
            try
            {
                PopulateQualityFields(reading);
                _provenance.MarkQualityEditedManually(); // manual test input is never DEVICE provenance
            }
            finally
            {
                _suppressProvenanceTracking = false;
            }

            ManualAnalyserResultTextBlock.Text =
                $"Parsed OK - Fat {reading.Fat}%  SNF {reading.Snf}%  CLR {reading.Clr}  " +
                $"Water {reading.OptionalParameters!["Water"]}%  Protein {reading.OptionalParameters!["Protein"]}%. " +
                "Values above have been filled in as MANUAL test input - proceed to a quality decision below.";
            ManualAnalyserResultTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        catch (DeviceParseException ex)
        {
            ManualAnalyserResultTextBlock.Text = $"Could not parse: {ex.Message}";
            ManualAnalyserResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
    }

    private void PopulateQualityFields(CCMC.Domain.ValueObjects.MilkQualityReading quality)
    {
        FatTextBox.Text = quality.Fat.ToString(CultureInfo.InvariantCulture);
        SnfTextBox.Text = quality.Snf.ToString(CultureInfo.InvariantCulture);
        ClrTextBox.Text = quality.Clr?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        WaterTextBox.Text = TryGetOptionalParameter(quality, "Water")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ProteinTextBox.Text = TryGetOptionalParameter(quality, "Protein")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        if (quality.Temperature is { } temperature)
        {
            TemperatureTextBox.Text = temperature.ToString(CultureInfo.InvariantCulture);
        }
        RawAnalyserPayloadTextBox.Text = quality.RawData is { Length: > 0 } raw
            ? System.Text.Encoding.ASCII.GetString(raw)
            : string.Empty;
    }

    private static decimal? TryGetOptionalParameter(CCMC.Domain.ValueObjects.MilkQualityReading quality, string key) =>
        quality.OptionalParameters is { } parameters && parameters.TryGetValue(key, out var value) ? value : null;

    private async void AcceptButton_Click(object sender, RoutedEventArgs e) => await DecideAsync(ReceptionDecision.Accept);

    private async void HoldButton_Click(object sender, RoutedEventArgs e) => await DecideAsync(ReceptionDecision.Hold);

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DecisionReasonTextBox.Text))
        {
            ResultTextBlock.Text = "A reason is required to reject a reception.";
            ResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        await DecideAsync(null); // null Decision signals REJECT - see DecideAsync
    }

    /// <summary>
    /// Shared validation + save path for all three decision buttons. A null
    /// <paramref name="decision"/> means REJECT, which - per
    /// TransactionStatus's own invariant (Rejected only via override of a
    /// Hold, unchanged) - is not a ReceptionDecision value; it is handled by
    /// ReceptionWorkflowService.RejectAtReceptionAsync instead (see its doc
    /// comment).
    /// </summary>
    private async Task DecideAsync(ReceptionDecision? decision)
    {
        var session = _sessionStore.Current;
        if (session is null)
        {
            ResultTextBlock.Text = "No active session.";
            return;
        }

        if (CentreComboBox.SelectedItem is not ChillingCentre centre ||
            SourceComboBox.SelectedItem is not Source source ||
            VehicleComboBox.SelectedItem is not Vehicle vehicle)
        {
            ResultTextBlock.Text = "Select a centre, source, and vehicle first.";
            return;
        }

        if (!TryParseDecimal(QuantityTextBox.Text, out var quantity) ||
            !TryParseDecimal(FatTextBox.Text, out var fat) ||
            !TryParseDecimal(SnfTextBox.Text, out var snf) ||
            !TryParseDecimal(TemperatureTextBox.Text, out var temperature))
        {
            ResultTextBlock.Text = "Quantity, FAT, SNF, and Temperature must all be valid numbers.";
            return;
        }

        var clr = TryParseDecimal(ClrTextBox.Text, out var clrValue) ? clrValue : (decimal?)null;
        var water = TryParseDecimal(WaterTextBox.Text, out var waterValue) ? waterValue : (decimal?)null;
        var protein = TryParseDecimal(ProteinTextBox.Text, out var proteinValue) ? proteinValue : (decimal?)null;
        var rawAnalyserPayload = string.IsNullOrWhiteSpace(RawAnalyserPayloadTextBox.Text) ? null : RawAnalyserPayloadTextBox.Text;

        SetDecisionButtonsEnabled(false);
        try
        {
            // Resolved from actual edit history (ReadingProvenanceTracker), not
            // just "was a device read ever attempted" - editing a device-sourced
            // field after the fact correctly demotes the transaction to MANUAL
            // (see ReadingProvenanceTracker's doc comment for why a single
            // transaction-level ReadingSource cannot represent a true mix).
            var readingSource = _provenance.Resolve();

            var input = new SaveReceptionInput(
                centre.Id, source.Id, vehicle.Id, session.User.Id,
                quantity, fat, snf, temperature, readingSource,
                decision ?? ReceptionDecision.Hold, // ignored by RejectAtReceptionAsync when decision is null
                clr, water, protein, rawAnalyserPayload,
                string.IsNullOrWhiteSpace(DecisionReasonTextBox.Text) ? null : DecisionReasonTextBox.Text);

            var result = decision is { } d
                ? await _receptionWorkflowService.ValidateAndSaveAsync(input with { Decision = d }, CancellationToken.None)
                : await _receptionWorkflowService.RejectAtReceptionAsync(input, DecisionReasonTextBox.Text, CancellationToken.None);

            ResultTextBlock.Text = result.WasNewlyCreated
                ? $"Saved locally. Status: {result.Transaction.Status}. Rate: {result.Transaction.Rate:F2}  Amount: {result.Transaction.Amount:F2}. Queued for cloud sync."
                : "This reception was already saved (duplicate save prevented).";
            ResultTextBlock.Foreground = result.Transaction.Status switch
            {
                TransactionStatus.Hold => System.Windows.Media.Brushes.DarkOrange,
                TransactionStatus.Rejected => System.Windows.Media.Brushes.Red,
                _ => System.Windows.Media.Brushes.Green,
            };
        }
        catch (IdempotencyKeyConflictException ex)
        {
            ResultTextBlock.Text = $"Save failed: {ex.Message}";
            ResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        catch (Exception ex)
        {
            ResultTextBlock.Text = $"Save failed: {ex.Message}";
            ResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            SetDecisionButtonsEnabled(true);
        }
    }

    private void SetDecisionButtonsEnabled(bool enabled)
    {
        AcceptButton.IsEnabled = enabled;
        HoldButton.IsEnabled = enabled;
        RejectButton.IsEnabled = enabled;
    }

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
