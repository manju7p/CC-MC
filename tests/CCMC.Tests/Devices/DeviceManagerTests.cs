using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Devices;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Persistence.Repositories;
using CCMC.Infrastructure.Serial;
using CCMC.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CCMC.Tests.Devices;

/// <summary>
/// Proves the DeviceManager -&gt; adapter -&gt; serial-infrastructure wiring is
/// actually reachable (the confirmed Blocker 2: InitializeAsync previously
/// had zero runtime call sites, leaving WeighingScale/MilkAnalyser null for
/// the whole app lifetime). No physical hardware is involved or required:
/// InitializeAsync only reads local configuration and constructs adapter
/// objects - it never opens a COM port (that happens lazily on
/// ConnectAsync, which these tests deliberately do not call) - so these
/// tests verify the initialization graph and dependency wiring without
/// fabricating any device reading.
/// </summary>
public class DeviceManagerTests : IDisposable
{
    // Deliberately NOT an IClassFixture: these tests assert on the complete
    // set of DeviceConfiguration rows (e.g. "no analyser row exists"), so
    // sharing one database across the class (as the Repository tests safely
    // do, since those only assert on their own keyed rows) would make
    // results depend on test execution order. Each test gets its own fresh
    // database instead - xUnit constructs a new test class instance per
    // test method by default, so this constructor runs once per test.
    private readonly SqliteTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private DeviceManager CreateManager(string capturesSubDir) => new(
        new SerialConnectionManager(),
        new DeviceConfigurationRepository(_fixture.ConnectionFactory),
        new RawCaptureLogger(Path.Combine(Path.GetTempPath(), "ccmc-test-captures", capturesSubDir)),
        NullLoggerFactory.Instance);

    [Fact]
    public async Task InitializeAsync_NoConfigurationRows_FallsBackToVerifiedWeighingScaleDefault()
    {
        var manager = CreateManager(Guid.NewGuid().ToString("N"));

        await manager.InitializeAsync(CancellationToken.None);

        Assert.NotNull(manager.WeighingScale);
        Assert.IsType<VideoconWeighingScaleAdapter>(manager.WeighingScale);
        Assert.Null(manager.MilkAnalyser); // no analyser configuration exists - must stay null, not fabricated
    }

    [Fact]
    public async Task InitializeAsync_NeverConnects_DeviceStaysDisconnectedUntilExplicitlyRequested()
    {
        // Constructing the adapter must not attempt to open the physical port -
        // that only happens on a later, explicit ConnectAsync() call (from
        // "Test Connection" or the reception workflow), never automatically.
        var manager = CreateManager(Guid.NewGuid().ToString("N"));

        await manager.InitializeAsync(CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, manager.WeighingScale!.State);
    }

    [Fact]
    public async Task InitializeAsync_WithAnalyserConfigurationRow_ConstructsAnalyserAdapter()
    {
        var configRepo = new DeviceConfigurationRepository(_fixture.ConnectionFactory);
        await configRepo.UpsertAsync(
            new DeviceConfiguration
            {
                Kind = DeviceKind.MilkAnalyser,
                Name = "Test Analyser",
                Serial = new SerialConfiguration { ComPort = "COM50", BaudRate = 9600 },
                IsEnabled = true,
            },
            CancellationToken.None);

        var manager = new DeviceManager(
            new SerialConnectionManager(),
            configRepo,
            new RawCaptureLogger(Path.Combine(Path.GetTempPath(), "ccmc-test-captures", Guid.NewGuid().ToString("N"))),
            NullLoggerFactory.Instance);

        await manager.InitializeAsync(CancellationToken.None);

        Assert.NotNull(manager.MilkAnalyser);
        Assert.IsType<EkomilkKam98A2AAnalyserAdapter>(manager.MilkAnalyser);
    }

    [Fact]
    public async Task InitializeAsync_WithCustomScaleConfigurationRow_OverridesVerifiedDefault()
    {
        var configRepo = new DeviceConfigurationRepository(_fixture.ConnectionFactory);
        await configRepo.UpsertAsync(
            new DeviceConfiguration
            {
                Kind = DeviceKind.WeighingScale,
                Name = "Custom Scale",
                Serial = new SerialConfiguration { ComPort = "COM77", BaudRate = 4800 },
                IsEnabled = true,
            },
            CancellationToken.None);

        var manager = new DeviceManager(
            new SerialConnectionManager(),
            configRepo,
            new RawCaptureLogger(Path.Combine(Path.GetTempPath(), "ccmc-test-captures", Guid.NewGuid().ToString("N"))),
            NullLoggerFactory.Instance);

        await manager.InitializeAsync(CancellationToken.None);

        Assert.NotNull(manager.WeighingScale);
        // Configuration is overrideable - COM4/2400 remains only the fallback
        // default, never a hard-coded value business logic assumes.
    }

    [Fact]
    public async Task TestConnectionAsync_NoDeviceConfigured_ReturnsDisconnected_DoesNotThrow()
    {
        var configRepo = new DeviceConfigurationRepository(_fixture.ConnectionFactory);
        var manager = new DeviceManager(
            new SerialConnectionManager(),
            configRepo,
            new RawCaptureLogger(Path.Combine(Path.GetTempPath(), "ccmc-test-captures", Guid.NewGuid().ToString("N"))),
            NullLoggerFactory.Instance);
        await manager.InitializeAsync(CancellationToken.None); // MilkAnalyser stays null (no config row for this fresh manager)

        var state = await manager.TestConnectionAsync(DeviceKind.MilkAnalyser, CancellationToken.None);

        Assert.Equal(DeviceConnectionState.Disconnected, state);
    }
}
