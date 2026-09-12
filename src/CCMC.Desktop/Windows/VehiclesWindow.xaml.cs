using System.Globalization;
using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Application.MasterData;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using CCMC.Domain.Entities;

namespace CCMC.Desktop.Windows;

public partial class VehiclesWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly IVehicleRepository _vehicleRepository;
    private readonly ICloudApiClient _cloudApiClient;
    private readonly MasterDataSyncService _masterDataSyncService;

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
        VehiclesGrid.ItemsSource = all;

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
}
