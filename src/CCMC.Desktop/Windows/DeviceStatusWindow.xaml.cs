using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Domain.Enums;

namespace CCMC.Desktop.Windows;

public partial class DeviceStatusWindow : Window
{
    private readonly IDeviceManager _deviceManager;

    public DeviceStatusWindow(IDeviceManager deviceManager)
    {
        InitializeComponent();
        _deviceManager = deviceManager;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        ScaleStateTextBlock.Text = $"State: {_deviceManager.WeighingScale?.State.ToString() ?? "Not configured"}";
        ScaleDetailTextBlock.Text = "Videocon Precision Systems weighing scale. Protocol decoder not yet " +
            "established - a successful connection test proves the COM port opens; reading a weight will " +
            "still report 'unavailable' until a verified protocol exists (see CLAUDE.md).";

        AnalyserStateTextBlock.Text = $"State: {_deviceManager.MilkAnalyser?.State.ToString() ?? "Not configured"}";
        AnalyserDetailTextBlock.Text = _deviceManager.MilkAnalyser is null
            ? "No milk analyser configuration has been entered yet - see Device Configuration."
            : "Milk analyser configured. No verified protocol decoder exists yet (see CLAUDE.md).";
    }

    private async void TestScaleButton_Click(object sender, RoutedEventArgs e)
    {
        TestScaleButton.IsEnabled = false;
        try
        {
            var state = await _deviceManager.TestConnectionAsync(DeviceKind.WeighingScale, CancellationToken.None);
            ScaleStateTextBlock.Text = $"State: {state}";
        }
        finally
        {
            TestScaleButton.IsEnabled = true;
        }
    }

    private async void TestAnalyserButton_Click(object sender, RoutedEventArgs e)
    {
        TestAnalyserButton.IsEnabled = false;
        try
        {
            var state = await _deviceManager.TestConnectionAsync(DeviceKind.MilkAnalyser, CancellationToken.None);
            AnalyserStateTextBlock.Text = $"State: {state}";
        }
        finally
        {
            TestAnalyserButton.IsEnabled = true;
        }
    }
}
