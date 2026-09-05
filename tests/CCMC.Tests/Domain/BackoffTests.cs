using CCMC.Domain.Sync;
using Xunit;

namespace CCMC.Tests.Domain;

public class BackoffTests
{
    [Fact]
    public void Compute_IsDeterministicExponential()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromMinutes(5);

        Assert.Equal(TimeSpan.FromSeconds(1), Backoff.Compute(1, baseDelay, max));
        Assert.Equal(TimeSpan.FromSeconds(2), Backoff.Compute(2, baseDelay, max));
        Assert.Equal(TimeSpan.FromSeconds(4), Backoff.Compute(3, baseDelay, max));
        Assert.Equal(TimeSpan.FromSeconds(8), Backoff.Compute(4, baseDelay, max));
    }

    [Fact]
    public void Compute_NeverExceedsMaxDelay()
    {
        var result = Backoff.Compute(20, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact]
    public void Compute_SameAttemptCount_AlwaysProducesSameDelay()
    {
        var first = Backoff.Compute(5);
        var second = Backoff.Compute(5);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_AttemptCountBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Compute(0));
    }
}
