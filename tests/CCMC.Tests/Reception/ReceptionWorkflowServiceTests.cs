using CCMC.Application.Reception;
using CCMC.Domain.Entities;
using CCMC.Domain.Enums;
using CCMC.Infrastructure.Common;
using CCMC.Infrastructure.Devices;
using CCMC.Infrastructure.Logging;
using CCMC.Infrastructure.Persistence.Repositories;
using CCMC.Infrastructure.Serial;
using CCMC.Tests.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CCMC.Tests.Reception;

/// <summary>
/// Proves the "auto-accept is retained but dormant, the operator's explicit
/// ACCEPT/HOLD/REJECT decision is authoritative" behavior required by
/// CLAUDE.md/STATUS.md "Milk Analyser + Quality Decision Flow" - real SQLite,
/// per this repo's testing philosophy (CLAUDE.md "Two dependency tiers").
/// </summary>
public class ReceptionWorkflowServiceTests : IDisposable
{
    private readonly SqliteTestFixture _fixture = new();
    private readonly QualityRuleRepository _qualityRuleRepository;
    private readonly ReceptionRepository _receptionRepository;
    private readonly AuditLogRepository _auditLogRepository;
    private readonly ReceptionWorkflowService _service;

    public ReceptionWorkflowServiceTests()
    {
        _qualityRuleRepository = new QualityRuleRepository(_fixture.ConnectionFactory);
        _receptionRepository = new ReceptionRepository(_fixture.ConnectionFactory);
        _auditLogRepository = new AuditLogRepository(_fixture.ConnectionFactory);

        var deviceManager = new DeviceManager(
            new SerialConnectionManager(),
            new DeviceConfigurationRepository(_fixture.ConnectionFactory),
            new RawCaptureLogger(Path.Combine(Path.GetTempPath(), "ccmc-test-captures", Guid.NewGuid().ToString("N"))),
            NullLoggerFactory.Instance);

        _service = new ReceptionWorkflowService(
            deviceManager, _qualityRuleRepository, _receptionRepository, _auditLogRepository,
            new GuidIdempotencyKeyGenerator(), new SystemClock(), NullLogger<ReceptionWorkflowService>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private async Task SeedRulesAsync(decimal fatMin = 3.0m, decimal fatMax = 6.0m)
    {
        await _qualityRuleRepository.ReplaceAllAsync(
            [
                new QualityRule { Id = 1, Parameter = QualityParameter.Fat, MinValue = fatMin, MaxValue = fatMax, CentreId = null },
                new QualityRule { Id = 2, Parameter = QualityParameter.Snf, MinValue = 8.0m, MaxValue = 9.5m, CentreId = null },
                new QualityRule { Id = 3, Parameter = QualityParameter.Temperature, MinValue = 0m, MaxValue = 10m, CentreId = null },
            ],
            CancellationToken.None);
    }

    private static SaveReceptionInput Input(ReceptionDecision decision, decimal fat = 4.5m, string? reason = null) => new(
        CentreId: 1, SourceId: 1, VehicleId: 1, OperatorUserId: 1,
        QuantityKg: 45.5m, Fat: fat, Snf: 9.0m, Temperature: 5.0m,
        ReadingSource: ReadingSource.Manual, Decision: decision,
        Clr: 28.4m, Water: 1.21m, Protein: 3.24m, RawAnalyserPayload: "(03900830283801210000032404503)",
        OperatorReason: reason);

    [Fact]
    public async Task ValidateAndSaveAsync_WithinLimits_AcceptDecision_SavesAccepted()
    {
        await SeedRulesAsync();

        var result = await _service.ValidateAndSaveAsync(Input(ReceptionDecision.Accept), CancellationToken.None);

        Assert.Equal(TransactionStatus.Accepted, result.Transaction.Status);
        Assert.Equal(28.4m, result.Transaction.Clr);
        Assert.Equal("(03900830283801210000032404503)", result.Transaction.RawAnalyserPayload);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WithinLimits_OperatorStillMustPressHold_NeverAutoAccepted()
    {
        // Fat/Snf/Temperature are all comfortably within the seeded ranges -
        // the automatic (dormant) suggestion would be ACCEPTED - but the
        // operator explicitly chose HOLD. That choice must win: this is the
        // whole point of "auto-accept retained but dormant."
        await SeedRulesAsync();

        var result = await _service.ValidateAndSaveAsync(Input(ReceptionDecision.Hold), CancellationToken.None);

        Assert.Equal(TransactionStatus.Hold, result.Transaction.Status);
        Assert.Contains("overrides the automatic quality suggestion of Accepted", result.Transaction.Reason);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_OutOfRange_OperatorAccepts_OverridesAutomaticHoldSuggestion()
    {
        // Fat is out of range (0.5, seeded range is 3.0-6.0) - the automatic
        // suggestion would be HOLD - but the operator explicitly chose ACCEPT.
        await SeedRulesAsync();

        var result = await _service.ValidateAndSaveAsync(Input(ReceptionDecision.Accept, fat: 0.5m), CancellationToken.None);

        Assert.Equal(TransactionStatus.Accepted, result.Transaction.Status);
        Assert.Contains("overrides the automatic quality suggestion of Hold", result.Transaction.Reason);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_OutOfRange_OperatorHolds_KeepsAutomaticReason()
    {
        await SeedRulesAsync();

        var result = await _service.ValidateAndSaveAsync(Input(ReceptionDecision.Hold, fat: 0.5m), CancellationToken.None);

        Assert.Equal(TransactionStatus.Hold, result.Transaction.Status);
        Assert.Contains("Fat", result.Transaction.Reason);
    }

    [Fact]
    public async Task RejectAtReceptionAsync_SavesAsHoldThenOverridesToRejected()
    {
        await SeedRulesAsync();

        var result = await _service.RejectAtReceptionAsync(
            Input(ReceptionDecision.Accept), "Visibly adulterated - high water content", CancellationToken.None);

        Assert.Equal(TransactionStatus.Rejected, result.Transaction.Status);
        Assert.Equal("Visibly adulterated - high water content", result.Transaction.Reason);

        // Preserves the existing "Rejected is only reachable via override of a Hold" invariant -
        // this exercised ValidateAndSaveAsync(Hold) THEN OverrideAsync(Rejected), not a direct create.
        var reloaded = await _receptionRepository.GetByLocalIdAsync(result.Transaction.LocalId, CancellationToken.None);
        Assert.Equal(TransactionStatus.Rejected, reloaded!.Status);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_OperatorReasonSupplied_IsIncludedInStoredReason()
    {
        await SeedRulesAsync();

        var result = await _service.ValidateAndSaveAsync(
            Input(ReceptionDecision.Hold, reason: "Smells off"), CancellationToken.None);

        Assert.Contains("Smells off", result.Transaction.Reason);
    }
}
