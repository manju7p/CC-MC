using System.Windows;
using CCMC.Application.Abstractions;

namespace CCMC.Desktop.Windows;

public partial class VehiclesWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly IVehicleRepository _vehicleRepository;

    public VehiclesWindow(ISessionStore sessionStore, IChillingCentreRepository centreRepository, IVehicleRepository vehicleRepository)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _vehicleRepository = vehicleRepository;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var session = _sessionStore.Current;
        if (session is null) return;

        var centres = await _centreRepository.ListAsync(CancellationToken.None);
        var accessibleIds = session.User.CentreAccess.AllCentres
            ? centres.Select(c => c.Id)
            : session.User.CentreAccess.CentreIds;

        var all = new List<Domain.Entities.Vehicle>();
        foreach (var centreId in accessibleIds)
        {
            all.AddRange(await _vehicleRepository.ListByCentreAsync(centreId, CancellationToken.None));
        }
        VehiclesGrid.ItemsSource = all;
    }
}
