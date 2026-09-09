using CCMC.Domain.ValueObjects;

namespace CCMC.Domain.Devices;

public interface IWeighingScale : IDevice
{
    /// <summary>
    /// Reads one weight from the scale. Throws DeviceProtocolNotEstablishedException
    /// if the concrete adapter's protocol decoder is not yet implemented (see
    /// CCMC.Infrastructure's VideoconWeighingScaleAdapter) - callers (the
    /// reception workflow) must catch this and fall back to manual entry
    /// (BRD v2 section 15, "Manual Fallback"), never crash.
    /// </summary>
    Task<WeightReading> ReadWeightAsync(CancellationToken cancellationToken);
}
