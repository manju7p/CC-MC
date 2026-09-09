using CCMC.Domain.ValueObjects;

namespace CCMC.Domain.Devices;

public interface IMilkAnalyser : IDevice
{
    /// <summary>See IWeighingScale.ReadWeightAsync's doc comment - same contract.</summary>
    Task<MilkQualityReading> ReadQualityAsync(CancellationToken cancellationToken);
}
