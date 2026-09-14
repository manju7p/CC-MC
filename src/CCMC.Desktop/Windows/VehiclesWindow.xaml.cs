using System.Globalization;
using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Application.MasterData;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;

namespace CCMC.Desktop.Windows;

public partial class VehiclesWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly ICloudApiClient _cloudApiClient;
    private readonly MasterDataSyncService _masterDataSyncService;

    private List<Vehicle> _allVehicles = [];

    public VehiclesWindow(
        ISessionStore sessionStore,
        IChillingCentreRepository centreRepository,
        IVehicleRepository vehicleRepository,
        ICloudApiClient cloudApiClient,
        MasterDataSyncService masterDataSyncService)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _vehicleRepository = vehicleRepository;
        _cloudApiClient = cloudApiClient;
        _masterDataSyncService = masterDataSyncService;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var session = _sessionStore.Current;
        if (session is null) return;

        var centres = await _centreRepository.ListAsync(CancellationToken.None);
        var accessible = session.User.CentreAccess.AllCentres
            ? centres
            : centres.Where(c => session.User.CentreAccess.CentreIds.Contains(c.Id)).ToList();

        var all = new List<Vehicle>();
        foreach (var centre in accessible)
        {
            all.AddRange(await _vehicleRepository.ListByCentreAsync(centre.Id, CancellationToken.None));
        }
        _allVehicles = all;
        ApplySearchFilter();

        // Client-side visibility only (see SourcesWindow's identical comment) -
        // the server independently enforces VEHICLE_CREATE regardless.
        var canCreate = !session.IsOffline && session.User.Permissions.Contains(PermissionCodes.VehicleCreate);
        AddVehiclePanel.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
        if (canCreate)
        {
            AddVehicleCentreComboBox.ItemsSource = accessible;
            if (accessible.Count > 0) AddVehicleCentreComboBox.SelectedIndex = 0;
        }
    }

    private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplySearchFilter();

    private void ApplySearchFilter()
    {
        if (!IsLoaded) return;

        var search = SearchTextBox.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(search)
            ? _allVehicles
            : _allVehicles.Where(v =>
                v.VehicleNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (v.TankerNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (v.DriverName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        VehiclesItemsControl.ItemsSource = filtered
            .OrderBy(v => v.VehicleNumber, StringComparer.OrdinalIgnoreCase)
            .Select((v, i) => VehicleRow.From(v, i))
            .ToList();

        var showEmpty = filtered.Count == 0;
        EmptyStatePanel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = showEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateBodyTextBlock.Text = _allVehicles.Count == 0
            ? "No vehicles are cached for your accessible centre(s) yet. Sign in online to sync the latest master data."
            : "No vehicles match this search.";
    }

    private async void AddVehicleButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _sessionStore.Current;
        if (session is null || session.IsOffline)
        {
            AddVehicleResultTextBlock.Text = "You must be signed in online to add a vehicle.";
            AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        if (AddVehicleCentreComboBox.SelectedItem is not ChillingCentre centre)
        {
            AddVehicleResultTextBlock.Text = "Select a chilling centre.";
            AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        var vehicleNumber = AddVehicleNumberTextBox.Text.Trim();
        if (string.IsNullOrEmpty(vehicleNumber))
        {
            AddVehicleResultTextBlock.Text = "Vehicle Number is required.";
            AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        decimal? capacityKg = null;
        if (!string.IsNullOrWhiteSpace(AddVehicleCapacityTextBox.Text))
        {
            if (!decimal.TryParse(AddVehicleCapacityTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                AddVehicleResultTextBlock.Text = "Capacity must be a valid number.";
                AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }
            capacityKg = parsed;
        }

        AddVehicleButton.IsEnabled = false;
        try
        {
            var request = new CreateVehicleRequestDto
            {
                VehicleNumber = vehicleNumber,
                TankerNumber = string.IsNullOrWhiteSpace(AddVehicleTankerNumberTextBox.Text) ? null : AddVehicleTankerNumberTextBox.Text.Trim(),
                DriverName = string.IsNullOrWhiteSpace(AddVehicleDriverNameTextBox.Text) ? null : AddVehicleDriverNameTextBox.Text.Trim(),
                DriverMobile = string.IsNullOrWhiteSpace(AddVehicleDriverMobileTextBox.Text) ? null : AddVehicleDriverMobileTextBox.Text.Trim(),
                CapacityKg = capacityKg,
                CentreId = centre.Id,
            };

            var result = await _cloudApiClient.CreateVehicleAsync(session.AccessToken, request, CancellationToken.None);
            if (result.Outcome != CloudMutationOutcome.Success)
            {
                AddVehicleResultTextBlock.Text = $"Could not add vehicle: {result.ErrorMessage}";
                AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            // Refresh the full local master-data cache so the new vehicle is
            // immediately selectable in Reception.
            await _masterDataSyncService.PullAsync(session.AccessToken, CancellationToken.None);
            await LoadAsync();

            AddVehicleNumberTextBox.Text = string.Empty;
            AddVehicleTankerNumberTextBox.Text = string.Empty;
            AddVehicleCapacityTextBox.Text = string.Empty;
            AddVehicleDriverNameTextBox.Text = string.Empty;
            AddVehicleDriverMobileTextBox.Text = string.Empty;
            AddVehicleResultTextBlock.Text = $"Vehicle '{result.Vehicle!.VehicleNumber}' added.";
            AddVehicleResultTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        finally
        {
            AddVehicleButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Display-shaping only for VehicleRowTemplate - formats an existing
    /// <see cref="CCMC.Domain.Entities.Vehicle"/> for the card-row list. No business/
    /// validation logic here (CLAUDE.md "business logic stays out of the UI").
    /// </summary>
    private sealed class VehicleRow
    {
        public required string VehicleNumber { get; init; }
        public required string TankerNumberText { get; init; }
        public required string DriverNameText { get; init; }
        public required string DriverMobileText { get; init; }
        public required string CapacityText { get; init; }
        public required string StatusText { get; init; }
        public required string StatusGlyph { get; init; }
        public required Style StatusChipBorderStyle { get; init; }
        public required Style StatusChipIconStyle { get; init; }
        public required Style StatusChipTextStyle { get; init; }
        public required bool IsEven { get; init; }

        public static VehicleRow From(Vehicle v, int index)
        {
            var isActive = v.Status == RecordStatus.Active;
            var (glyph, borderKey, iconKey, textKey) = isActive
                ? ("", "SuccessChipBorder", "SuccessChipIcon", "SuccessChipText")
                : ("", "NeutralChipBorder", "NeutralChipIcon", "NeutralChipText");

            return new VehicleRow
            {
                VehicleNumber = v.VehicleNumber,
                TankerNumberText = string.IsNullOrWhiteSpace(v.TankerNumber) ? "No tanker #" : $"Tanker {v.TankerNumber}",
                DriverNameText = string.IsNullOrWhiteSpace(v.DriverName) ? "No driver on file" : v.DriverName,
                DriverMobileText = string.IsNullOrWhiteSpace(v.DriverMobile) ? "—" : v.DriverMobile,
                CapacityText = v.CapacityKg is { } capacity ? $"{capacity:F0} kg" : "—",
                StatusText = v.Status.ToString().ToUpperInvariant(),
                StatusGlyph = glyph,
                StatusChipBorderStyle = (Style)System.Windows.Application.Current.FindResource(borderKey),
                StatusChipIconStyle = (Style)System.Windows.Application.Current.FindResource(iconKey),
                StatusChipTextStyle = (Style)System.Windows.Application.Current.FindResource(textKey),
                IsEven = index % 2 == 1,
            };
        }
    }
}
