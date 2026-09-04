/**
 * Deterministic exponential backoff, deliberately a pure function of
 * attemptCount (not wall-clock or random) so retry behavior is exactly
 * reproducible in tests ("make retry behavior deterministic enough to
 * test" - Checkpoint 3 instructions). No jitter: at the transaction
 * volume of a single chilling centre (a handful of receptions per hour),
 * the thundering-herd problem jitter exists to solve does not apply - one
 * gateway, one outbox, no fleet of clients hitting the same endpoint at
 * the same instant. Adding jitter now would only make tests harder to
 * assert on for no real benefit (Rule 11: don't over-engineer).
 */
export interface BackoffOptions {
  /** Delay before the first retry (attemptCount === 1), in milliseconds. */
  baseDelayMs: number;
  /** Upper bound the delay is capped at, no matter how many attempts. */
  maxDelayMs: number;
}

export const DEFAULT_BACKOFF_OPTIONS: BackoffOptions = {
  baseDelayMs: 1_000, // 1 second
  maxDelayMs: 5 * 60_000, // 5 minutes
};

/**
 * attemptCount is the count AFTER the failed attempt that triggered this
 * backoff (i.e. call with the post-increment value). attemptCount=1 means
 * "the first attempt just failed" -> baseDelayMs. Doubles per subsequent
 * attempt, capped at maxDelayMs.
 */
export function computeBackoffDelayMs(attemptCount: number, options: BackoffOptions = DEFAULT_BACKOFF_OPTIONS): number {
  if (attemptCount < 1) {
    throw new Error(`computeBackoffDelayMs: attemptCount must be >= 1, got ${attemptCount}`);
  }
  const exponential = options.baseDelayMs * Math.pow(2, attemptCount - 1);
  return Math.min(exponential, options.maxDelayMs);
}
