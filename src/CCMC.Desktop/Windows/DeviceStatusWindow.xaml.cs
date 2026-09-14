using System.Windows;
using CCMC.Application.Abstractions;
using CCMC.Desktop.Controls;
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
        SetState(ScaleStateHost, _deviceManager.WeighingScale?.State);
        ScaleDetailTextBlock.Text = "Videocon Precision Systems weighing scale. Protocol decoder verified against " +
            "the physical device - a successful connection test opens the COM port and reads the actual weight.";

        SetState(AnalyserStateHost, _deviceManager.MilkAnalyser?.State);
        AnalyserDetailTextBlock.Text = _deviceManager.MilkAnalyser is null
            ? "No milk analyser configuration has been entered yet - see Device Configuration."
            : "Ekomilk Milkana KAM98-2A. Payload decode is verified against real device output; the physical " +
              "serial connection itself has not yet been tested against the hardware - see STATUS.md.";
    }

    private static void SetState(System.Windows.Controls.StackPanel host, DeviceConnectionState? state)
    {
        host.Children.Clear();
        if (state is null)
        {
            host.Children.Add(StatusChip.Create("NOT CONFIGURED", ChipKind.Neutral));
            return;
        }

        var kind = state switch
        {
            DeviceConnectionState.Connected => ChipKind.Success,
            DeviceConnectionState.Connecting => ChipKind.Info,
            DeviceConnectionState.Error => ChipKind.Danger,
            _ => ChipKind.Neutral,
        };
        host.Children.Add(StatusChip.Create(state.ToString()!.ToUpperInvariant(), kind));
    }

    private async void TestScaleButton_Click(object sender, RoutedEventArgs e)
    {
        TestScaleButton.IsEnabled = false;
        try
        {
            var state = await _deviceManager.TestConnectionAsync(DeviceKind.WeighingScale, CancellationToken.None);
            SetState(ScaleStateHost, state);
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
            SetState(AnalyserStateHost, state);
        }
        finally
        {
            TestAnalyserButton.IsEnabled = true;
        }
    }
}
