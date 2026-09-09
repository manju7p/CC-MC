using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Serial;

namespace CCMC.Desktop.Windows;

/// <summary>
/// Full serial configuration UI (BRD "Device Configuration" screen +
/// this session's explicit multi-centre requirement): COM port is
/// dynamically enumerated (never a fixed list), baud rate covers the
/// standard set, data bits/parity/stop bits/flow control are all
/// selectable. COM4/2400/8-N-1 remains only the verified DEFAULT for the
/// weighing scale (SerialConfiguration.VerifiedWeighingScaleDefault) -
/// never a hard-coded assumption a different centre's hardware must match.
/// </summary>
public partial class DeviceConfigurationWindow : Window
{
    private static readonly int[] StandardBaudRates = [110, 300, 600, 1200, 2400, 4800, 9600, 14400, 19200, 38400, 57600, 115200];
    private static readonly int[] StandardDataBits = [5, 6, 7, 8];

    private static readonly ComboOption<SerialStopBits>[] StopBitsOptions =
    [
        new("1", SerialStopBits.One),
        new("1.5", SerialStopBits.OnePointFive),
        new("2", SerialStopBits.Two),
    ];

    private static readonly ComboOption<SerialParity>[] ParityOptions =
    [
        new("None", SerialParity.None),
        new("Even", SerialParity.Even),
        new("Odd", SerialParity.Odd),
        new("Mark", SerialParity.Mark),
        new("Space", SerialParity.Space),
    ];

    private static readonly ComboOption<SerialFlowControl>[] FlowControlOptions =
    [
        new("None", SerialFlowControl.None),
        new("RTS/CTS (hardware)", SerialFlowControl.RequestToSend),
        new("XON/XOFF (software)", SerialFlowControl.XOnXOff),
        new("RTS/CTS + XON/XOFF", SerialFlowControl.RequestToSendXOnXOff),
    ];

    private readonly IDeviceConfigurationRepository _configurationRepository;
    private readonly SerialConnectionManager _connectionManager;

    public DeviceConfigurationWindow(IDeviceConfigurationRepository configurationRepository, SerialConnectionManager connectionManager)
    {
        InitializeComponent();
        _configurationRepository = configurationRepository;
        _connectionManager = connectionManager;

        DeviceKindComboBox.ItemsSource = Enum.GetValues<DeviceKind>();
        BaudRateComboBox.ItemsSource = StandardBaudRates;
        DataBitsComboBox.ItemsSource = StandardDataBits;
        StopBitsComboBox.ItemsSource = StopBitsOptions;
        ParityComboBox.ItemsSource = ParityOptions;
        FlowControlComboBox.ItemsSource = FlowControlOptions;

        RefreshPorts();

        Loaded += (_, _) => DeviceKindComboBox.SelectedIndex = 0;
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        var currentSelection = ComPortComboBox.Text;
        ComPortComboBox.ItemsSource = SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        ComPortComboBox.Text = currentSelection;
    }

    private async void DeviceKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceKindComboBox.SelectedItem is not DeviceKind kind) return;

        var existing = await _configurationRepository.GetAsync(kind, CancellationToken.None);
        var serial = existing?.Serial
            ?? (kind == DeviceKind.WeighingScale ? SerialConfiguration.VerifiedWeighingScaleDefault : null);

        NameTextBox.Text = existing?.Name ?? DefaultNameFor(kind);
        ComPortComboBox.Text = serial?.ComPort ?? string.Empty;
        BaudRateComboBox.Text = (serial?.BaudRate ?? 9600).ToString();
        DataBitsComboBox.SelectedItem = serial?.DataBits ?? 8;
        StopBitsComboBox.SelectedItem = StopBitsOptions.First(o => o.Value == (serial?.StopBits ?? SerialStopBits.One));
        ParityComboBox.SelectedItem = ParityOptions.First(o => o.Value == (serial?.Parity ?? SerialParity.None));
        FlowControlComboBox.SelectedItem = FlowControlOptions.First(o => o.Value == (serial?.FlowControl ?? SerialFlowControl.None));
        ReadTimeoutTextBox.Text = (serial?.ReadTimeoutMs ?? 3000).ToString();
        EnabledCheckBox.IsChecked = existing?.IsEnabled ?? true;

        StatusTextBlock.Text = string.Empty;
    }

    private static string DefaultNameFor(DeviceKind kind) => kind switch
    {
        DeviceKind.WeighingScale => "Videocon Precision Systems (verified default) - weighing scale",
        DeviceKind.MilkAnalyser => "Milk Analyser",
        _ => kind.ToString(),
    };

    private bool TryBuildConfiguration(out DeviceConfiguration configuration, out string error)
    {
        configuration = null!;
        error = string.Empty;

        if (DeviceKindComboBox.SelectedItem is not DeviceKind kind)
        {
            error = "Select a device.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ComPortComboBox.Text))
        {
            error = "Select or enter a COM port.";
            return false;
        }

        if (!int.TryParse(BaudRateComboBox.Text, out var baudRate) || baudRate <= 0)
        {
            error = "Baud rate must be a positive number.";
            return false;
        }

        if (DataBitsComboBox.SelectedItem is not int dataBits)
        {
            error = "Select a data bits value.";
            return false;
        }

        if (StopBitsComboBox.SelectedItem is not ComboOption<SerialStopBits> stopBits ||
            ParityComboBox.SelectedItem is not ComboOption<SerialParity> parity ||
            FlowControlComboBox.SelectedItem is not ComboOption<SerialFlowControl> flowControl)
        {
            error = "Select stop bits, parity, and flow control.";
            return false;
        }

        if (!int.TryParse(ReadTimeoutTextBox.Text, out var readTimeoutMs) || readTimeoutMs <= 0)
        {
            error = "Read timeout must be a positive number of milliseconds.";
            return false;
        }

        configuration = new DeviceConfiguration
        {
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(NameTextBox.Text) ? DefaultNameFor(kind) : NameTextBox.Text,
            IsEnabled = EnabledCheckBox.IsChecked ?? true,
            Serial = new SerialConfiguration
            {
                ComPort = ComPortComboBox.Text.Trim(),
                BaudRate = baudRate,
                DataBits = dataBits,
                StopBits = stopBits.Value,
                Parity = parity.Value,
                FlowControl = flowControl.Value,
                ReadTimeoutMs = readTimeoutMs,
            },
        };
        return true;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfiguration(out var configuration, out var error))
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = error;
            return;
        }

        await _configurationRepository.UpsertAsync(configuration, CancellationToken.None);

        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        StatusTextBlock.Text = "Saved. Restart the application for the new device configuration to take effect.";
    }

    /// <summary>
    /// Tests the currently entered (not-yet-saved) settings by opening the
    /// real serial port directly, then closing it immediately - this never
    /// invents a fake success. If the port is already owned by the app's
    /// active device manager (see SerialConnectionManager), that is reported
    /// clearly rather than as a generic failure - "Do not open the same COM
    /// port twice" is enforced by the same mechanism the running adapters use.
    /// </summary>
    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildConfiguration(out var configuration, out var error))
        {
            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            StatusTextBlock.Text = error;
            return;
        }

        TestConnectionButton.IsEnabled = false;
        try
        {
            SerialPortConnection connection;
            try
            {
                connection = _connectionManager.Acquire(configuration.Serial);
            }
            catch (SerialPortOwnershipException)
            {
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;
                StatusTextBlock.Text = $"Port {configuration.Serial.ComPort} is currently owned by an active device connection - stop using it there first.";
                return;
            }

            try
            {
                await connection.OpenAsync(CancellationToken.None);
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                StatusTextBlock.Text = $"Successfully opened {configuration.Serial.ComPort} at {configuration.Serial.BaudRate} baud.";
                await connection.CloseAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                StatusTextBlock.Text = $"Could not open {configuration.Serial.ComPort}: {ex.Message}";
            }
            finally
            {
                _connectionManager.Release(configuration.Serial.ComPort);
            }
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }
}
