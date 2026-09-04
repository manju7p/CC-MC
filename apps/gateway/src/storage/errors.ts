/** Thrown when this SQLite file's pinned gateway/centre identity doesn't match the loaded config. */
export class GatewayIdentityMismatchError extends Error {}

/**
 * Thrown when createLocalTransaction() is called with a localIdempotencyKey
 * that already exists, but with a DIFFERENT logical payload than the row
 * it was first created with.
 *
 * This is the deliberate line between two situations that must NOT be
 * treated the same way:
 *
 *  - same key + same payload -> a genuine retry of the same logical
 *    transaction (network hiccup, caller unsure whether an earlier call
 *    succeeded). Safe to treat as a no-op and return the existing row -
 *    this is what createLocalTransaction() does (see TEST 3).
 *  - same key + DIFFERENT payload -> a caller bug (or a key collision,
 *    astronomically unlikely with crypto.randomUUID() but not provable
 *    impossible): someone is reusing an idempotency key for what is
 *    actually a different transaction. Silently returning the ORIGINAL
 *    transaction here would be actively wrong - it would look like the
 *    new data was accepted when it was actually discarded, with no
 *    record anywhere that this happened. That is a data-integrity
 *    failure mode, not a convenience to preserve.
 *
 * `conflictingFields` lists exactly which fields differed, so a caller
 * (and a human debugging a log) can see precisely what didn't match
 * rather than just "something was different."
 */
export class IdempotencyKeyConflictError extends Error {
  constructor(
    public readonly localIdempotencyKey: string,
    public readonly conflictingFields: string[],
  ) {
    super(
      `localIdempotencyKey "${localIdempotencyKey}" was already used for a transaction with different data ` +
        `(conflicting field(s): ${conflictingFields.join(", ")}). An idempotency key must be generated once per ` +
        `logical transaction and only reused for retries of that SAME transaction - reusing it for different data ` +
        `is a bug in the caller, not a case this API resolves silently.`,
    );
    this.name = "IdempotencyKeyConflictError";
  }
}
