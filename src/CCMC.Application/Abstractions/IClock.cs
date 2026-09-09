namespace CCMC.Application.Abstractions;

/// <summary>Testability seam for "now" - never call DateTimeOffset.UtcNow directly in Application services.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
