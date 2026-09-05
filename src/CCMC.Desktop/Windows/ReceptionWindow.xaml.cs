using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Application.Reception;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;

namespace CCMC.Desktop.Windows;

public partial class ReceptionWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly ReceptionWorkflowService _receptionWorkflowService;

    private readonly ReadingProvenanceTracker _provenance = new();

    /// <summary>
    /// Guards the TextChanged handlers below against firing when THIS class
    /// programmatically sets a field's .Text after a device read - only a
    /// genuine operator keystroke should ever mark a reading as manually
    /// edited. See ReadDevicesButton_Click, which sets this around its own
    /// .Text assignments.
    /// </summary>
    private bool _suppressProvenanceTracking;

    public ReceptionWindow(
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

        Loaded += ReceptionWindow_Loaded;

        QuantityTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkWeightEditedManually(); };
        FatTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        SnfTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
        TemperatureTextBox.TextChanged += (_, _) => { if (!_suppressProvenanceTracking) _provenance.MarkQualityEditedManually(); };
    }

    private async void ReceptionWindow_Loaded(object sender, RoutedEventArgs e)
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
                    FatTextBox.Text = quality.Fat.ToString(CultureInfo.InvariantCulture);
                    SnfTextBox.Text = quality.Snf.ToString(CultureInfo.InvariantCulture);
                    TemperatureTextBox.Text = quality.Temperature.ToString(CultureInfo.InvariantCulture);
                    _provenance.RecordQualityFromDevice();
                    QualityStatusTextBlock.Text = "Quality read from analyser.";
                    QualityStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    QualityStatusTextBlock.Text = $"Analyser unavailable - enter FAT/SNF/Temperature manually. ({result.QualityUnavailableReason})";
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
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

        SaveButton.IsEnabled = false;
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
                quantity, fat, snf, temperature, readingSource);

            var result = await _receptionWorkflowService.ValidateAndSaveAsync(input, CancellationToken.None);

            ResultTextBlock.Text = result.WasNewlyCreated
                ? $"Saved locally. Status: {result.Transaction.Status}. Queued for cloud sync."
                : "This reception was already saved (duplicate save prevented).";
            ResultTextBlock.Foreground = result.Transaction.Status == TransactionStatus.Hold
                ? System.Windows.Media.Brushes.DarkOrange
                : System.Windows.Media.Brushes.Green;
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
            SaveButton.IsEnabled = true;
        }
    }

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
