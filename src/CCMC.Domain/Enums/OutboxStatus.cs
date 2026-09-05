namespace CCMC.Domain.Enums;

/// <summary>
/// PENDING -&gt; PROCESSING -&gt; SYNCED (terminal, success)
///               |
///               +-- (retryable failure) --&gt; PENDING (RetryWait is represented by
///                    OutboxRecord.NextAttemptAt being in the future, not a
///                    separate stored state - see BRD v2 Technical Design section 15;
///                    a status column value for it would just duplicate the same
///                    information NextAttemptAt already carries)
///               |
///               +-- (attempts exhausted, or a terminal/409 cloud response) --&gt; FAILED (terminal)
/// </summary>
public enum OutboxStatus
{
    Pending,
    Processing,
    Synced,
    Failed,
}
