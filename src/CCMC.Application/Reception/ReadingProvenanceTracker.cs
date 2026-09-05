using CCMC.Domain.Enums;

namespace CCMC.Application.Reception;

/// <summary>
/// Tracks whether the weight reading and the quality reading are each still
/// exactly what a device reported, so a saved transaction's ReadingSource
/// never overclaims DEVICE provenance after a human edits a value the device
/// supplied (the confirmed provenance bug: previously, editing a field after
/// a successful device read left the transaction reporting DEVICE even
/// though a human had touched the value).
///
/// The cloud/domain model has exactly one ReadingSource per transaction, not
/// one per field (verified during Phase 0 - see context.md "Cloud
/// Responsibilities"), so per-field provenance cannot be represented or
/// synced. Given that constraint, this class implements the least ambiguous
/// correct behavior: the transaction is DEVICE only if BOTH the weight
/// reading and the quality reading are still untouched since their last
/// device read; it is MANUAL the moment either one is edited by a human,
/// even if the other reading was never touched. This never overclaims DEVICE
/// provenance, which is the direction of error that would actually corrupt
/// the audit trail.
/// </summary>
public sealed class ReadingProvenanceTracker
{
    public bool WeightFromDevice { get; private set; }
    public bool QualityFromDevice { get; private set; }

    /// <summary>Call when a device read of the weight succeeds and the UI is about to populate the weight field from it.</summary>
    public void RecordWeightFromDevice() => WeightFromDevice = true;

    /// <summary>Call when a device read of FAT/SNF/Temperature succeeds and the UI is about to populate those fields from it.</summary>
    public void RecordQualityFromDevice() => QualityFromDevice = true;

    /// <summary>Call the moment a human edits the weight field, regardless of whether it was ever device-sourced.</summary>
    public void MarkWeightEditedManually() => WeightFromDevice = false;

    /// <summary>Call the moment a human edits any of FAT/SNF/Temperature, regardless of whether it was ever device-sourced.</summary>
    public void MarkQualityEditedManually() => QualityFromDevice = false;

    /// <summary>Call at the start of a fresh capture cycle (e.g. before a new "Read Devices" attempt).</summary>
    public void Reset()
    {
        WeightFromDevice = false;
        QualityFromDevice = false;
    }

    public ReadingSource Resolve() => WeightFromDevice && QualityFromDevice ? ReadingSource.Device : ReadingSource.Manual;
}
