using System.IO;
using System.Windows;
using System.Windows.Threading;
using CCMC.Application.Abstractions;
using CCMC.Desktop.Composition;
using CCMC.Desktop.Windows;
using CCMC.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CCMC.Desktop;

/// <summary>
/// The application's composition root. Builds the DI container, applies the
/// local SQLite schema, initializes the device manager (loads configuration
/// and constructs adapters - does not itself open any COM port, see
/// DeviceManager's doc comment), wires a process-wide unhandled-exception
/// guard (a failed device/sync/UI operation must never crash the whole app -
/// BRD v2 section 18/29), and opens the login window. No business logic
/// lives here.
/// </summary>
public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(Composition.AppPaths.RootDirectory);

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();
        services.AddCcmcServices(configuration);
        _serviceProvider = services.BuildServiceProvider();

        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("CCMC starting up.");

        _serviceProvider.GetRequiredService<SchemaMigrator>().Migrate();
        _logger.LogInformation("Local database schema is up to date ({DatabasePath}).", Composition.AppPaths.DatabasePath);

        // Loads device configuration and constructs the configured adapters
        // (Videocon scale / milk analyser) so the reception workflow can
        // actually reach them - without this call, IDeviceManager.WeighingScale/
        // MilkAnalyser stay null for the app's entire lifetime. This only reads
        // local SQLite config and constructs objects; it does not open any COM
        // port (that happens lazily on ConnectAsync), so it cannot fail due to
        // a physical device being absent - but is defensively wrapped anyway,
        // since a startup step must never prevent the login window from
        // appearing (BRD v2 "none of these conditions should crash the
        // application").
        try
        {
            await _serviceProvider.GetRequiredService<IDeviceManager>().InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device manager initialization failed - device features may be unavailable.");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var loginWindow = _serviceProvider.GetRequiredService<LoginWindow>();
        loginWindow.Show();

        _logger.LogInformation("CCMC startup complete.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("CCMC shutting down (exit code {ExitCode}).", e.ApplicationExitCode);
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Engineering rule: "none of these conditions should crash the application"
        // (BRD v2 section 20 / 29). Report and continue rather than let WPF tear
        // the process down - the operator can retry the action or restart manually
        // if the app is genuinely in a bad state.
        _logger?.LogError(e.Exception, "Unhandled exception on the UI dispatcher.");

        System.Windows.MessageBox.Show(
            $"An unexpected error occurred: {e.Exception.Message}\n\nThe application will continue running.",
            "CCMC - Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
