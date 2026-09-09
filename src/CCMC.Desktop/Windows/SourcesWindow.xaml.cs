using System.Windows;
using CCMC.Application.Abstractions;

namespace CCMC.Desktop.Windows;

public partial class SourcesWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;

    public SourcesWindow(ISessionStore sessionStore, IChillingCentreRepository centreRepository, ISourceRepository sourceRepository)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _sourceRepository = sourceRepository;
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

        var all = new List<Domain.Entities.Source>();
        foreach (var centreId in accessibleIds)
        {
            all.AddRange(await _sourceRepository.ListByCentreAsync(centreId, CancellationToken.None));
        }
        SourcesGrid.ItemsSource = all;
    }
}
