using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Application.MasterData;
using CCMC.Contracts.Auth;
using CCMC.Contracts.Dtos;
using CCMC.Domain.Entities;

namespace CCMC.Desktop.Windows;

public partial class SourcesWindow : Window
{
    private readonly ISessionStore _sessionStore;
    private readonly IChillingCentreRepository _centreRepository;
    private readonly ISourceRepository _sourceRepository;
    private readonly ICloudApiClient _cloudApiClient;
    private readonly MasterDataSyncService _masterDataSyncService;

    public SourcesWindow(
        ISessionStore sessionStore,
        IChillingCentreRepository centreRepository,
        ISourceRepository sourceRepository,
        ICloudApiClient cloudApiClient,
        MasterDataSyncService masterDataSyncService)
    {
        InitializeComponent();
        _sessionStore = sessionStore;
        _centreRepository = centreRepository;
        _sourceRepository = sourceRepository;
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

        var all = new List<Source>();
        foreach (var centre in accessible)
        {
            all.AddRange(await _sourceRepository.ListByCentreAsync(centre.Id, CancellationToken.None));
        }
        SourcesGrid.ItemsSource = all;

        // Client-side visibility only, matching this repo's existing UX-only
        // permission-check convention (CLAUDE.md "Security baseline") - the
        // server independently enforces SOURCE_CREATE regardless of whether
        // this panel is shown. An offline session also cannot create a
        // Source (no usable access token - see Session.IsOffline), so the
        // panel stays hidden then too, matching LoginWindow's own treatment
        // of an offline session as unable to reach the cloud at all.
        var canCreate = !session.IsOffline && session.User.Permissions.Contains(PermissionCodes.SourceCreate);
        AddSourcePanel.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
        if (canCreate)
        {
            AddSourceCentreComboBox.ItemsSource = accessible;
            if (accessible.Count > 0) AddSourceCentreComboBox.SelectedIndex = 0;
        }
    }

    private async void AddSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _sessionStore.Current;
        if (session is null || session.IsOffline)
        {
            AddSourceResultTextBlock.Text = "You must be signed in online to add a source.";
            AddSourceResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        if (AddSourceCentreComboBox.SelectedItem is not ChillingCentre centre)
        {
            AddSourceResultTextBlock.Text = "Select a chilling centre.";
            AddSourceResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        var code = AddSourceCodeTextBox.Text.Trim();
        var name = AddSourceNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name))
        {
            AddSourceResultTextBlock.Text = "Code and Name are required.";
            AddSourceResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            return;
        }

        AddSourceButton.IsEnabled = false;
        try
        {
            var request = new CreateSourceRequestDto
            {
                Code = code,
                Name = name,
                Location = string.IsNullOrWhiteSpace(AddSourceLocationTextBox.Text) ? null : AddSourceLocationTextBox.Text.Trim(),
                Contact = string.IsNullOrWhiteSpace(AddSourceContactTextBox.Text) ? null : AddSourceContactTextBox.Text.Trim(),
                MilkType = string.IsNullOrWhiteSpace(AddSourceMilkTypeTextBox.Text) ? null : AddSourceMilkTypeTextBox.Text.Trim(),
                CentreId = centre.Id,
            };

            var result = await _cloudApiClient.CreateSourceAsync(session.AccessToken, request, CancellationToken.None);
            if (result.Outcome != CloudMutationOutcome.Success)
            {
                AddSourceResultTextBlock.Text = $"Could not add source: {result.ErrorMessage}";
                AddSourceResultTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            // Refresh the full local master-data cache so the new source is
            // immediately selectable in Reception, per this task's explicit
            // requirement - not just appended to this window's own grid.
            await _masterDataSyncService.PullAsync(session.AccessToken, CancellationToken.None);
            await LoadAsync();

            AddSourceCodeTextBox.Text = string.Empty;
            AddSourceNameTextBox.Text = string.Empty;
            AddSourceLocationTextBox.Text = string.Empty;
            AddSourceContactTextBox.Text = string.Empty;
            AddSourceMilkTypeTextBox.Text = string.Empty;
            AddSourceResultTextBlock.Text = $"Source '{result.Source!.Name}' added.";
            AddSourceResultTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        finally
        {
            AddSourceButton.IsEnabled = true;
        }
    }
}
