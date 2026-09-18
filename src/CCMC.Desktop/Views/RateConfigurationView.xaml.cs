using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Application.MasterData;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.Services;

namespace CCMC.Desktop.Views;

/// <summary>
/// Manager/Admin-only Rate Configuration screen (BRD v5.0 section 25 "Milk Rate
/// Calculation" - PREFS_RATE_TYPE/VALUE1/VALUE2/TS). Root cause this screen fixes: before
/// this, there was no way for anyone to ever configure Value1/Value2/RateType/TsRate from
/// the Windows app at all (no UI, and ICloudApiClient had no PUT method) - the cloud's
/// PUT /rate-formula-settings endpoint and its RATE_FORMULA_CONFIGURE permission already
/// existed and were already tested, but nothing ever called it, so
/// IRateFormulaSettingsRepository.ResolveForCentreAsync always resolved to null and every
/// reception's Rate/Amount was always 0 (see RateCalculationService.Calculate's "config is
/// null -> Zero" branch) - not a calculation bug, a configuration-never-existed bug.
///
/// Nav visibility (Manager/Admin only, hidden for Operator) is enforced one level up in
/// MainWindow, same convention as Sources/Vehicles. The SAVE action here is additionally
/// gated on the actual RATE_FORMULA_CONFIGURE permission (defensive, UX-only - the server's
/// own [RequirePermission] + CentreAccessGuard on PUT /rate-formula-settings remains the
/// real authorization boundary, per CLAUDE.md "Security baseline").
/// </summary>
public partial class RateConfigurationView : UserControl
{
    private sealed record ModeOption(string Label, RateFormulaType Value);

    private static readonly ModeOption[] ModeOptions =
    [
        new("Fat vs SNF", RateFormulaType.FatVsSnf),
        new("TS Based", RateFormulaType.TsBased),
    ];

    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly IRateFormulaSettingsRepository _rateFormulaSettingsRepository;
    private readonly ICloudApiClient _cloudApiClient;
    private readonly MasterDataSyncService _masterDataSyncService;

    private List<ChillingCentre> _accessibleCentres = [];
    private List<RateFormulaSettings> _allSettings = [];

    /// <summary>Guards field TextChanged handlers from firing while THIS class is populating fields from a loaded/reloaded configuration - only a genuine operator keystroke should trigger anything downstream.</summary>
    private bool _suppressChangeHandling;

    public RateConfigurationView(
        ISessionStore sessionStore,
        IChillingCentreRepository centreRepository,
        IRateFormulaSettingsRepository rateFormulaSettingsRepository,
        ICloudApiClient cloudApiClient,
        MasterDataSyncService masterDataSyncService)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _rateFormulaSettingsRepository = rateFormulaSettingsRepository;
        _cloudApiClient = cloudApiClient;
        _masterDataSyncService = masterDataSyncService;

        ModeComboBox.ItemsSource = ModeOptions;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var session = _sessionStore.Current;
        if (session is null)
        {
            StatusTextBlock.Text = "No active session.";
            return;
        }

        var centres = await _centreRepository.ListAsync(CancellationToken.None);
        _accessibleCentres = (session.User.CentreAccess.AllCentres
            ? centres
            : centres.Where(c => session.User.CentreAccess.CentreIds.Contains(c.Id)).ToList())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _allSettings = (await _rateFormulaSettingsRepository.ListAsync(CancellationToken.None)).ToList();

        CentreComboBox.ItemsSource = _accessibleCentres;
        if (_accessibleCentres.Count > 0)
        {
            CentreComboBox.SelectedIndex = 0; // triggers CentreComboBox_SelectionChanged -> LoadSettingsForSelectedCentre
        }
        else
        {
            StatusTextBlock.Text = "You have no chilling centre assigned - rate configuration cannot be edited.";
        }
    }

    private void CentreComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSettingsForSelectedCentre();

    /// <summary>
    /// Loads whichever row exists for EXACTLY the selected centre (not the centre-over-
    /// global resolution ReceptionWorkflowService.CalculateRateAsync uses) - a Manager
    /// editing "their centre's configuration" must see and edit that centre's own row, not
    /// silently inherit-and-then-overwrite the shared global default.
    /// </summary>
    private void LoadSettingsForSelectedCentre()
    {
        if (CentreComboBox.SelectedItem is not ChillingCentre centre) return;

        var existing = _allSettings.FirstOrDefault(s => s.CentreId == centre.Id);

        _suppressChangeHandling = true;
        try
        {
            var mode = existing?.RateType ?? RateFormulaType.FatVsSnf;
            ModeComboBox.SelectedItem = ModeOptions.First(o => o.Value == mode);
            Value1TextBox.Text = existing?.Value1?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Value2TextBox.Text = existing?.Value2?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            TsRateTextBox.Text = existing?.TsRate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }
        finally
        {
            _suppressChangeHandling = false;
        }

        ApplyModeVisibility(mode: existing?.RateType ?? RateFormulaType.FatVsSnf);
        UpdateFormulaText();
        UpdatePreview();

        StatusTextBlock.Text = existing is null
            ? $"{centre.Name} has no rate configuration yet - fill in the parameters below and save."
            : $"Showing {centre.Name}'s current configuration.";
        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeComboBox.SelectedItem is not ModeOption option) return;
        ApplyModeVisibility(option.Value);
        UpdateFormulaText();
        if (!_suppressChangeHandling) UpdatePreview();
    }

    private void ApplyModeVisibility(RateFormulaType mode)
    {
        FatVsSnfPanel.Visibility = mode == RateFormulaType.FatVsSnf ? Visibility.Visible : Visibility.Collapsed;
        TsBasedPanel.Visibility = mode == RateFormulaType.TsBased ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Exact BRD v5.0 section 25.2/25.3 wording - never a paraphrase invented by this UI.
    /// </summary>
    private void UpdateFormulaText()
    {
        if (ModeComboBox.SelectedItem is not ModeOption option) return;

        FormulaTextBlock.Text = option.Value == RateFormulaType.FatVsSnf
            ? "Rate = (Value 1 + Value 2) x 0.22 x (FAT / 100)\n"
              + "     + (Value 1 + Value 2) x 0.36 x (SNF / 100)\n"
              + "     + 0.32\n\n"
              + "Amount = Rate x Weight"
            : "TS = FAT + SNF\n\n"
              + "Rate = (TS x TS Rate) / 100\n\n"
              + "Amount = Rate x Weight";
    }

    private void PreviewInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_suppressChangeHandling) UpdatePreview();
    }

    /// <summary>
    /// Uses RateCalculationService.Calculate directly - the exact same domain calculation
    /// ReceptionWorkflowService.CalculateRateAsync calls at real reception time - so this
    /// preview can never diverge from what a real reception with this configuration would
    /// actually produce. Reads whatever is currently typed in the edit fields (not yet
    /// saved), so a Manager can see the effect of a change before committing it.
    /// </summary>
    private void UpdatePreview()
    {
        if (ModeComboBox.SelectedItem is not ModeOption option)
        {
            PreviewRateTextBlock.Text = "0.00";
            PreviewAmountTextBlock.Text = "0.00";
            return;
        }

        var fat = TryParseDecimal(PreviewFatTextBox.Text, out var fatValue) ? fatValue : (decimal?)null;
        var snf = TryParseDecimal(PreviewSnfTextBox.Text, out var snfValue) ? snfValue : (decimal?)null;
        var weight = TryParseDecimal(PreviewWeightTextBox.Text, out var weightValue) ? weightValue : (decimal?)null;

        var value1 = TryParseDecimal(Value1TextBox.Text, out var v1) ? v1 : (decimal?)null;
        var value2 = TryParseDecimal(Value2TextBox.Text, out var v2) ? v2 : (decimal?)null;
        var tsRate = TryParseDecimal(TsRateTextBox.Text, out var ts) ? ts : (decimal?)null;

        var config = new RateFormulaConfig(option.Value, value1, value2, tsRate);
        var result = RateCalculationService.Calculate(new RateCalculationInput(fat, snf, weight), config);

        PreviewRateTextBlock.Text = result.Rate.ToString("F2", CultureInfo.InvariantCulture);
        PreviewAmountTextBlock.Text = result.Amount.ToString("F2", CultureInfo.InvariantCulture);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _sessionStore.Current;
        if (session is null || session.IsOffline)
        {
            SetStatus("You must be signed in online to save rate configuration.", isError: true);
            return;
        }

        // Defensive, UX-only - see this class's doc comment. The server's own
        // [RequirePermission(RateFormulaConfigure)] + CentreAccessGuard is the real check.
        if (!session.User.Permissions.Contains(PermissionCodes.RateFormulaConfigure))
        {
            SetStatus("Your role does not permit editing rate configuration.", isError: true);
            return;
        }

        if (CentreComboBox.SelectedItem is not ChillingCentre centre)
        {
            SetStatus("Select a chilling centre first.", isError: true);
            return;
        }

        if (ModeComboBox.SelectedItem is not ModeOption option)
        {
            SetStatus("Select a calculation mode first.", isError: true);
            return;
        }

        decimal? value1 = null, value2 = null, tsRate = null;

        if (option.Value == RateFormulaType.FatVsSnf)
        {
            // BRD v5.0 section 25.2: "Requires both Value1 and Value2 to be configured;
            // otherwise Rate/Amount = 0" - validated here so a Manager cannot silently save
            // a half-configured formula that would only be discovered later as "every
            // reception at this centre computes 0".
            if (!TryParseDecimal(Value1TextBox.Text, out var v1) || !TryParseDecimal(Value2TextBox.Text, out var v2))
            {
                SetStatus("Fat-vs-SNF mode requires both Value 1 and Value 2 to be valid numbers.", isError: true);
                return;
            }
            value1 = v1;
            value2 = v2;
        }
        else
        {
            // BRD v5.0 section 25.3: "Requires TS_Rate to be configured; otherwise Rate/Amount = 0".
            if (!TryParseDecimal(TsRateTextBox.Text, out var ts))
            {
                SetStatus("TS-based mode requires TS Rate to be a valid number.", isError: true);
                return;
            }
            tsRate = ts;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var request = new UpsertRateFormulaSettingsRequestDto
            {
                CentreId = centre.Id,
                RateType = option.Value == RateFormulaType.FatVsSnf
                    ? CCMC.Contracts.Enums.RateFormulaType.FAT_VS_SNF
                    : CCMC.Contracts.Enums.RateFormulaType.TS_BASED,
                Value1 = value1,
                Value2 = value2,
                TsRate = tsRate,
            };

            var result = await _cloudApiClient.UpdateRateFormulaSettingsAsync(session.AccessToken, request, CancellationToken.None);
            if (result.Outcome != CloudMutationOutcome.Success)
            {
                SetStatus($"Could not save rate configuration: {result.ErrorMessage}", isError: true);
                return;
            }

            // Refresh the full local master-data cache so ReceptionWorkflowService's own
            // rate resolution (used both by the Reception screen's live preview and by
            // ValidateAndSaveAsync at capture time) sees this change immediately - same
            // pattern as SourcesView/VehiclesView's own AddButton_Click.
            await _masterDataSyncService.PullAsync(session.AccessToken, CancellationToken.None);
            _allSettings = (await _rateFormulaSettingsRepository.ListAsync(CancellationToken.None)).ToList();

            SetStatus($"Saved rate configuration for {centre.Name}.", isError: false);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.Green;
    }

    private static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
