using CCMC.Application.Abstractions;
using CCMC.Application.Auth;
using CCMC.Application.MasterData;
using CCMC.Application.Reception;
using CCMC.Application.Sync;
using CCMC.Infrastructure.Auth;
using CCMC.Infrastructure.Common;
using CCMC.Infrastructure.Configuration;
using CCMC.Infrastructure.Devices;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Persistence;
using CCMC.Infrastructure.Persistence.Repositories;
using CCMC.Infrastructure.Serial;
using CCMC.Infrastructure.Sync;
using CCMC.Desktop.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CCMC.Desktop.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCcmcServices(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration["CloudApi:BaseUrl"]
            ?? throw new InvalidOperationException("Missing configuration: CloudApi:BaseUrl (see appsettings.json).");

        // Structured logging (BRD v2 section 19: Application/Device/Serial/Parser/
        // Reception/Synchronization/Security categories - each lives in its own
        // class today, so the ILogger<T> category name already carries that
        // grouping). Writes to AppPaths.LogsDirectory via a small, dependency-free
        // file provider (see FileLoggerProvider's doc comment for why no logging
        // package was added).
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new CCMC.Infrastructure.Logging.FileLoggerProvider(AppPaths.LogsDirectory));
        });

        services.AddSingleton(new CloudApiOptions { BaseUrl = new Uri(baseUrl) });

        services.AddHttpClient<ICloudApiClient, HttpCloudApiClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<CloudApiOptions>();
            client.BaseAddress = options.BaseUrl;
            client.Timeout = options.RequestTimeout;
        });

        // Persistence
        services.AddSingleton(new SqliteConnectionFactory(AppPaths.DatabasePath));
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<IReceptionRepository, ReceptionRepository>();
        services.AddSingleton<IOutboxRepository, OutboxRepository>();
        services.AddSingleton<IOverrideOutboxRepository, OverrideOutboxRepository>();
        services.AddSingleton<IChillingCentreRepository, ChillingCentreRepository>();
        services.AddSingleton<ISourceRepository, SourceRepository>();
        services.AddSingleton<IVehicleRepository, VehicleRepository>();
        services.AddSingleton<IQualityRuleRepository, QualityRuleRepository>();
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddSingleton<IDeviceConfigurationRepository, DeviceConfigurationRepository>();
        services.AddSingleton<IOfflineCredentialStore, OfflineCredentialStore>();

        // Devices / serial
        services.AddSingleton(new RawCaptureLogger(AppPaths.CapturesDirectory));
        services.AddSingleton<SerialConnectionManager>();
        services.AddSingleton<IDeviceManager, DeviceManager>();

        // Cross-cutting
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdempotencyKeyGenerator, GuidIdempotencyKeyGenerator>();
        services.AddSingleton<ISessionStore, InMemorySessionStore>();

        // Application services
        services.AddSingleton<AuthenticationService>();
        services.AddSingleton<ReceptionWorkflowService>();
        services.AddSingleton<MasterDataSyncService>();
        services.AddSingleton(new SyncEngineOptions());
        services.AddSingleton<SyncEngineService>();

        // Windows - transient, so each navigation opens a fresh instance with fresh data.
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainWindow>();
        services.AddTransient<ReceptionWindow>();
        services.AddTransient<ReceptionHistoryWindow>();
        services.AddTransient<SourcesWindow>();
        services.AddTransient<VehiclesWindow>();
        services.AddTransient<DeviceStatusWindow>();
        services.AddTransient<DeviceConfigurationWindow>();
        services.AddTransient<SyncStatusWindow>();
        services.AddTransient<SettingsWindow>();

        return services;
    }
}
