using System.Windows;
using CCMC.Desktop.Composition;
using CCMC.Infrastructure.Configuration;

namespace CCMC.Desktop.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow(CloudApiOptions cloudApiOptions)
    {
        InitializeComponent();
        CloudApiUrlTextBlock.Text = cloudApiOptions.BaseUrl.ToString();
        DatabasePathTextBlock.Text = AppPaths.DatabasePath;
        CapturesPathTextBlock.Text = AppPaths.CapturesDirectory;
    }
}
