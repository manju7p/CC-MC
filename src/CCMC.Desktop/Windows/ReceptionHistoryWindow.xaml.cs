using System.Windows;
using CCMC.Application.Abstractions;

namespace CCMC.Desktop.Windows;

public partial class ReceptionHistoryWindow : Window
{
    private readonly IReceptionRepository _receptionRepository;

    public ReceptionHistoryWindow(IReceptionRepository receptionRepository)
    {
        InitializeComponent();
        _receptionRepository = receptionRepository;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        TransactionsGrid.ItemsSource = await _receptionRepository.ListRecentAsync(200, CancellationToken.None);
    }
}
