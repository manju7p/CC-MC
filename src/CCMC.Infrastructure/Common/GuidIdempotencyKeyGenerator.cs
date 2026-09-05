using CCMC.Application.Abstractions;

namespace CCMC.Infrastructure.Common;

/// <summary>Crypto-random UUID v4, same mechanism the legacy gateway design documented (context.md) - no extra dependency needed.</summary>
public sealed class GuidIdempotencyKeyGenerator : IIdempotencyKeyGenerator
{
    public string NewKey() => $"ccmc-desktop-{Guid.NewGuid():N}";
}
