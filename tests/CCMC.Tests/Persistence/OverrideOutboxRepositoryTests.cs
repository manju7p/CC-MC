using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Persistence.Repositories;
using Xunit;

namespace CCMC.Tests.Persistence;

public class OverrideOutboxRepositoryTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public OverrideOutboxRepositoryTests(SqliteTestFixture fixture) => _fixture = fixture;

    /// <summary>Seeds a reception, optionally already "synced" (has a CloudTransactionId), then applies a HOLD -&gt; override to it.</summary>
    private async Task<(ReceptionRepository ReceptionRepo, OverrideOutboxRepository OverrideRepo, long TransactionLocalId, long OverrideOutboxId)>
        SeedOverrideAsync(bool parentAlreadySynced)
    {
        var receptionRepo = new ReceptionRepository(_fixture.ConnectionFactory);
        var overrideRepo = new OverrideOutboxRepository(_fixture.ConnectionFactory);
        var now = DateTimeOffset.UtcNow;
        var key = $"test-{Guid.NewGuid():N}";

        var created = await receptionRepo.CreateAsync(
            new MilkReceptionTransaction
            {
                CentreId = 1, SourceId = 1, VehicleId = 1, OperatorUserId = 1,
                QuantityKg = 40m, Fat = 2.0m, Snf = 7.0m, Temperature = 4.0m,
                Status = TransactionStatus.Hold, ReadingSource = ReadingSource.Manual,
                LocalIdempotencyKey = key, CapturedAt = now, CreatedAt = now,
            },
            CancellationToken.None);

        if (parentAlreadySynced)
        {
            await receptionRepo.UpdateAfterSyncAsync(created.Transaction.LocalId, 9001, "BLR-CC-01-9001", CancellationToken.None);
        }

        var outboxRecord = await receptionRepo.ApplyOverrideAsync(
            new TransactionOverride
            {
                TransactionLocalId = created.Transaction.LocalId,
                OriginalStatus = TransactionStatus.Hold,
                NewStatus = TransactionStatus.Accepted,
                PerformedByUserId = 2,
                Reason = "Manager review",
                CreatedAt = now,
            },
            CancellationToken.None);

        return (receptionRepo, overrideRepo, created.Transaction.LocalId, outboxRecord.Id);
    }

    [Fact]
    public async Task ApplyOverrideAsync_CreatesPendingOverrideOutboxRow()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: true);

        var eligible = await overrideRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        Assert.Contains(eligible, r => r.Id == outboxId && r.NewStatus == TransactionStatus.Accepted);
    }

    [Fact]
    public async Task GetEligibleAsync_ParentNotYetSynced_ExcludesTheRow()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: false);

        var eligible = await overrideRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        // The parent reception has no CloudTransactionId yet - the override
        // must not be eligible for cloud sync before its parent is synced.
        Assert.DoesNotContain(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task TryClaimAsync_DoubleClaimGuard_SecondClaimFails()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: true);

        var first = await overrideRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await overrideRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task MarkSyncedAsync_RemovesFromEligibleSet()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: true);
        await overrideRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        await overrideRepo.MarkSyncedAsync(outboxId, CancellationToken.None);

        var eligible = await overrideRepo.GetEligibleAsync(DateTimeOffset.UtcNow.AddDays(1), 10, CancellationToken.None);
        Assert.DoesNotContain(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task MarkFailedAsync_IsTerminal_NeverReturnsToEligibleSet()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: true);
        await overrideRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        // Simulates the documented conservative policy: a terminal/4xx cloud
        // response marks the override FAILED for manual review, never retried.
        await overrideRepo.MarkFailedAsync(outboxId, "409 - not on hold, ambiguous retry", CancellationToken.None);

        var eligible = await overrideRepo.GetEligibleAsync(DateTimeOffset.UtcNow.AddDays(1), 10, CancellationToken.None);
        Assert.DoesNotContain(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task RecoverStaleProcessingAsync_RequeuesOrphanedRows()
    {
        var (_, overrideRepo, _, outboxId) = await SeedOverrideAsync(parentAlreadySynced: true);
        await overrideRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        var recovered = await overrideRepo.RecoverStaleProcessingAsync(DateTimeOffset.UtcNow, TimeSpan.Zero, CancellationToken.None);

        Assert.True(recovered >= 1);
        var eligible = await overrideRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);
        Assert.Contains(eligible, r => r.Id == outboxId);
    }
}
