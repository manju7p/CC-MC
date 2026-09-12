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
        ScaleDetailTextBlock.Text = "Videocon Precision Systems weighing scale. Protocol decoder verified against " +
            "the physical device - a successful connection test opens the COM port and reads the actual weight.";

        AnalyserStateTextBlock.Text = $"State: {_deviceManager.MilkAnalyser?.State.ToString() ?? "Not configured"}";
        AnalyserDetailTextBlock.Text = _deviceManager.MilkAnalyser is null
            ? "No milk analyser configuration has been entered yet - see Device Configuration."
            : "Ekomilk Milkana KAM98-2A. Payload decode is verified against real device output; the physical " +
              "serial connection itself has not yet been tested against the hardware - see STATUS.md.";
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
