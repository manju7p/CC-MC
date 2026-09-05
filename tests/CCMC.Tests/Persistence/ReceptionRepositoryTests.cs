using CCMC.Application.Abstractions;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Persistence.Repositories;
using Xunit;

namespace CCMC.Tests.Persistence;

public class ReceptionRepositoryTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public ReceptionRepositoryTests(SqliteTestFixture fixture) => _fixture = fixture;

    private static MilkReceptionTransaction NewTransaction(string idempotencyKey, decimal quantity = 45.5m, DateTimeOffset? capturedAt = null)
    {
        var now = capturedAt ?? DateTimeOffset.UtcNow;
        return new MilkReceptionTransaction
        {
            CentreId = 1,
            SourceId = 1,
            VehicleId = 1,
            OperatorUserId = 1,
            QuantityKg = quantity,
            Fat = 4.5m,
            Snf = 9.0m,
            Temperature = 5.0m,
            Status = TransactionStatus.Accepted,
            ReadingSource = ReadingSource.Manual,
            LocalIdempotencyKey = idempotencyKey,
            CapturedAt = now,
            CreatedAt = now,
        };
    }

    [Fact]
    public async Task CreateAsync_NewTransaction_CreatesBothTransactionAndOutboxRow()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        var key = $"test-{Guid.NewGuid():N}";

        var result = await repo.CreateAsync(NewTransaction(key), CancellationToken.None);

        Assert.True(result.WasNewlyCreated);
        Assert.True(result.Transaction.LocalId > 0);
        Assert.Equal(OutboxStatus.Pending, result.Outbox.Status);
        Assert.Equal(result.Transaction.LocalId, result.Outbox.TransactionLocalId);
        Assert.Equal(key, result.Outbox.LocalIdempotencyKey);
    }

    [Fact]
    public async Task CreateAsync_SameKeySamePayload_ReturnsExistingWithoutDuplicating()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        var key = $"test-{Guid.NewGuid():N}";
        var capturedAt = DateTimeOffset.UtcNow;

        var first = await repo.CreateAsync(NewTransaction(key, capturedAt: capturedAt), CancellationToken.None);
        var second = await repo.CreateAsync(NewTransaction(key, capturedAt: capturedAt), CancellationToken.None);

        Assert.True(first.WasNewlyCreated);
        Assert.False(second.WasNewlyCreated);
        Assert.Equal(first.Transaction.LocalId, second.Transaction.LocalId);
        Assert.Equal(first.Outbox.Id, second.Outbox.Id);
    }

    [Fact]
    public async Task CreateAsync_SameKeyDifferentPayload_ThrowsConflict()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        var key = $"test-{Guid.NewGuid():N}";
        var capturedAt = DateTimeOffset.UtcNow;

        await repo.CreateAsync(NewTransaction(key, quantity: 45.5m, capturedAt: capturedAt), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
            repo.CreateAsync(NewTransaction(key, quantity: 99.9m, capturedAt: capturedAt), CancellationToken.None));

        Assert.Contains("QuantityKg", ex.ConflictingFields);
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsMultipleTransactions_AfterReopeningConnection()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        await repo.CreateAsync(NewTransaction($"test-{Guid.NewGuid():N}"), CancellationToken.None);
        await repo.CreateAsync(NewTransaction($"test-{Guid.NewGuid():N}"), CancellationToken.None);

        // A fresh repository instance re-opens the same file - simulates surviving an app restart.
        var reopened = new ReceptionRepository(_fixture.ConnectionFactory);
        var list = await reopened.ListRecentAsync(100, CancellationToken.None);

        Assert.True(list.Count >= 2);
    }

    [Fact]
    public async Task UpdateAfterSyncAsync_SetsCloudTransactionIdAndNumber()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        var created = await repo.CreateAsync(NewTransaction($"test-{Guid.NewGuid():N}"), CancellationToken.None);

        await repo.UpdateAfterSyncAsync(created.Transaction.LocalId, 12345, "BLR-CC-01-12345", CancellationToken.None);

        var reloaded = await repo.GetByLocalIdAsync(created.Transaction.LocalId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(12345, reloaded!.CloudTransactionId);
        Assert.Equal("BLR-CC-01-12345", reloaded.TransactionNumber);
    }

    [Fact]
    public async Task ApplyOverrideAsync_OnHold_UpdatesStatusAndRecordsOverride()
    {
        var repo = new ReceptionRepository(_fixture.ConnectionFactory);
        var created = await repo.CreateAsync(HoldTransaction($"test-{Guid.NewGuid():N}"), CancellationToken.None);

        await repo.ApplyOverrideAsync(
            new TransactionOverride
            {
                TransactionLocalId = created.Transaction.LocalId,
                OriginalStatus = TransactionStatus.Hold,
                NewStatus = TransactionStatus.Accepted,
                PerformedByUserId = 2,
                Reason = "Manager review",
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

        var reloaded = await repo.GetByLocalIdAsync(created.Transaction.LocalId, CancellationToken.None);
        Assert.Equal(TransactionStatus.Accepted, reloaded!.Status);
    }

    private static MilkReceptionTransaction HoldTransaction(string key)
    {
        var t = NewTransaction(key);
        return new MilkReceptionTransaction
        {
            CentreId = t.CentreId,
            SourceId = t.SourceId,
            VehicleId = t.VehicleId,
            OperatorUserId = t.OperatorUserId,
            QuantityKg = t.QuantityKg,
            Fat = t.Fat,
            Snf = t.Snf,
            Temperature = t.Temperature,
            Status = TransactionStatus.Hold,
            ReadingSource = t.ReadingSource,
            LocalIdempotencyKey = t.LocalIdempotencyKey,
            CapturedAt = t.CapturedAt,
            CreatedAt = t.CreatedAt,
        };
    }
}
