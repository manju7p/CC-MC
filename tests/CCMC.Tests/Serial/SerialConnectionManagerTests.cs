using CCMC.Domain.Enums;
using CCMC.Domain.ValueObjects;
using CCMC.Infrastructure.Serial;
using Xunit;

namespace CCMC.Tests.Serial;

/// <summary>
/// No real hardware/COM port is opened by these tests - Acquire()'s ownership
/// bookkeeping is tested independently of SerialPort.Open() actually
/// succeeding, since a CI/dev machine may not have COM4 (or any port) present.
/// </summary>
public class SerialConnectionManagerTests
{
    [Fact]
    public void Acquire_SamePortTwice_ThrowsOwnershipException()
    {
        var manager = new SerialConnectionManager();
        var config = new SerialConfiguration { ComPort = "COM99", BaudRate = 2400 };

        var first = manager.Acquire(config);
        try
        {
            Assert.Throws<SerialPortOwnershipException>(() => manager.Acquire(config));
        }
        finally
        {
            manager.Release(config.ComPort);
            first.Dispose();
        }
    }

    [Fact]
    public void Release_ThenAcquireAgain_Succeeds()
    {
        var manager = new SerialConnectionManager();
        var config = new SerialConfiguration { ComPort = "COM98", BaudRate = 2400 };

        var first = manager.Acquire(config);
        manager.Release(config.ComPort);

        var second = manager.Acquire(config);
        manager.Release(config.ComPort);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void IsOwned_ReflectsCurrentOwnershipState()
    {
        var manager = new SerialConnectionManager();
        var config = new SerialConfiguration { ComPort = "COM97", BaudRate = 2400 };

        Assert.False(manager.IsOwned(config.ComPort));
        manager.Acquire(config);
        Assert.True(manager.IsOwned(config.ComPort));
        manager.Release(config.ComPort);
        Assert.False(manager.IsOwned(config.ComPort));
    }

    [Fact]
    public void VerifiedWeighingScaleDefault_MatchesPhysicallyVerifiedConfiguration()
    {
        var config = SerialConfiguration.VerifiedWeighingScaleDefault;

        Assert.Equal("COM4", config.ComPort);
        Assert.Equal(2400, config.BaudRate);
        Assert.Equal(8, config.DataBits);
        Assert.Equal(SerialStopBits.One, config.StopBits);
        Assert.Equal(SerialParity.None, config.Parity);
        Assert.Equal(SerialFlowControl.None, config.FlowControl);
    }
}
