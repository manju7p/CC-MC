using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Persistence.Repositories;
using Xunit;

namespace CCMC.Tests.Persistence;

public class OutboxRepositoryTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public OutboxRepositoryTests(SqliteTestFixture fixture) => _fixture = fixture;

    private async Task<(ReceptionRepository ReceptionRepo, OutboxRepository OutboxRepo, long OutboxId)> SeedPendingAsync()
    {
        var receptionRepo = new ReceptionRepository(_fixture.ConnectionFactory);
        var outboxRepo = new OutboxRepository(_fixture.ConnectionFactory);
        var key = $"test-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var created = await receptionRepo.CreateAsync(
            new MilkReceptionTransaction
            {
                CentreId = 1,
                SourceId = 1,
                VehicleId = 1,
                OperatorUserId = 1,
                QuantityKg = 40m,
                Fat = 4.0m,
                Snf = 8.5m,
                Temperature = 4.0m,
                Status = TransactionStatus.Accepted,
                ReadingSource = ReadingSource.Manual,
                LocalIdempotencyKey = key,
                CapturedAt = now,
                CreatedAt = now,
            },
            CancellationToken.None);

        return (receptionRepo, outboxRepo, created.Outbox.Id);
    }

    [Fact]
    public async Task GetEligibleAsync_ReturnsPendingItemsDueNow()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();

        var eligible = await outboxRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);

        Assert.Contains(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task TryClaimAsync_TransitionsPendingToProcessing_AndSecondClaimFails()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();

        var firstClaim = await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);
        var secondClaim = await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(firstClaim);
        Assert.False(secondClaim); // already PROCESSING - structural double-claim guard
    }

    [Fact]
    public async Task MarkSyncedAsync_RemovesFromEligibleSet()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();
        await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        await outboxRepo.MarkSyncedAsync(outboxId, CancellationToken.None);

        var eligible = await outboxRepo.GetEligibleAsync(DateTimeOffset.UtcNow.AddDays(1), 10, CancellationToken.None);
        Assert.DoesNotContain(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task MarkRetryAsync_ReturnsToPendingWithFutureNextAttempt_NotEligibleImmediately()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();
        await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        var future = DateTimeOffset.UtcNow.AddMinutes(5);
        await outboxRepo.MarkRetryAsync(outboxId, future, "simulated transient failure", CancellationToken.None);

        var eligibleNow = await outboxRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);
        Assert.DoesNotContain(eligibleNow, r => r.Id == outboxId);

        var eligibleLater = await outboxRepo.GetEligibleAsync(future.AddSeconds(1), 10, CancellationToken.None);
        Assert.Contains(eligibleLater, r => r.Id == outboxId && r.AttemptCount == 1);
    }

    [Fact]
    public async Task MarkFailedAsync_IsTerminal_NeverReturnsToEligibleSet()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();
        await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        await outboxRepo.MarkFailedAsync(outboxId, "terminal cloud conflict", CancellationToken.None);

        var eligible = await outboxRepo.GetEligibleAsync(DateTimeOffset.UtcNow.AddDays(1), 10, CancellationToken.None);
        Assert.DoesNotContain(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task RecoverStaleProcessingAsync_RequeuesOrphanedProcessingRowsToPending()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();
        await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);

        // Simulates a fresh process start (staleThreshold = 0) after a crash mid-PROCESSING.
        var recovered = await outboxRepo.RecoverStaleProcessingAsync(DateTimeOffset.UtcNow, TimeSpan.Zero, CancellationToken.None);

        Assert.True(recovered >= 1);
        var eligible = await outboxRepo.GetEligibleAsync(DateTimeOffset.UtcNow, 10, CancellationToken.None);
        Assert.Contains(eligible, r => r.Id == outboxId);
    }

    [Fact]
    public async Task CountPendingAsync_CountsBothPendingAndProcessing()
    {
        var (_, outboxRepo, outboxId) = await SeedPendingAsync();
        var beforeClaim = await outboxRepo.CountPendingAsync(CancellationToken.None);

        await outboxRepo.TryClaimAsync(outboxId, DateTimeOffset.UtcNow, CancellationToken.None);
        var afterClaim = await outboxRepo.CountPendingAsync(CancellationToken.None);

        Assert.Equal(beforeClaim, afterClaim); // PROCESSING still counts as "not yet synced"
    }
}
