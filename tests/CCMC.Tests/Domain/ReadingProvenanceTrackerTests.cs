using CCMC.Application.Reception;
using CCMC.Domain.Enums;
using Xunit;

namespace CCMC.Tests.Domain;

public class ReadingProvenanceTrackerTests
{
    [Fact]
    public void Resolve_BeforeAnyDeviceRead_IsManual()
    {
        var tracker = new ReadingProvenanceTracker();
        Assert.Equal(ReadingSource.Manual, tracker.Resolve());
    }

    [Fact]
    public void Resolve_BothWeightAndQualityFromDevice_IsDevice()
    {
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();
        tracker.RecordQualityFromDevice();

        Assert.Equal(ReadingSource.Device, tracker.Resolve());
    }

    [Fact]
    public void Resolve_OnlyWeightFromDevice_QualityNeverRead_IsManual()
    {
        // e.g. no analyser configured - quality was always manual entry.
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();

        Assert.Equal(ReadingSource.Manual, tracker.Resolve());
    }

    [Fact]
    public void Resolve_WeightEditedAfterDeviceRead_DemotesWholeTransactionToManual()
    {
        // This is the exact bug the review found: editing a device-populated
        // field after the fact must not leave the transaction reporting DEVICE.
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();
        tracker.RecordQualityFromDevice();

        tracker.MarkWeightEditedManually();

        Assert.Equal(ReadingSource.Manual, tracker.Resolve());
    }

    [Fact]
    public void Resolve_QualityEditedAfterDeviceRead_DemotesWholeTransactionToManual()
    {
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();
        tracker.RecordQualityFromDevice();

        tracker.MarkQualityEditedManually();

        Assert.Equal(ReadingSource.Manual, tracker.Resolve());
    }

    [Fact]
    public void Reset_ClearsBothFlags_ReturningToManual()
    {
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();
        tracker.RecordQualityFromDevice();
        Assert.Equal(ReadingSource.Device, tracker.Resolve());

        tracker.Reset();

        Assert.Equal(ReadingSource.Manual, tracker.Resolve());
        Assert.False(tracker.WeightFromDevice);
        Assert.False(tracker.QualityFromDevice);
    }

    [Fact]
    public void RecordAfterEdit_CanRestoreDeviceProvenance_OnANewCaptureCycle()
    {
        // A fresh "Read Devices" click (Reset, then new Record* calls) legitimately
        // re-establishes DEVICE provenance even if a previous cycle was edited.
        var tracker = new ReadingProvenanceTracker();
        tracker.RecordWeightFromDevice();
        tracker.MarkWeightEditedManually();

        tracker.Reset();
        tracker.RecordWeightFromDevice();
        tracker.RecordQualityFromDevice();

        Assert.Equal(ReadingSource.Device, tracker.Resolve());
    }
}
