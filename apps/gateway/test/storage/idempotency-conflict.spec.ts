import { IdempotencyKeyConflictError } from "../../src/storage/errors";
import { createTestStorage, sampleTransactionInput } from "../support/test-storage";

describe("SqliteLocalStorage - idempotency payload-conflict semantics", () => {
  it("same key + same payload (a genuine retry) returns the existing transaction safely", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "same-payload-key" });
    const first = await handle.storage.createLocalTransaction(input);
    // Reusing the exact same input object (or an equal-value copy) is the
    // "retry" case - a network hiccup, or the caller unsure whether its
    // first call landed.
    const second = await handle.storage.createLocalTransaction({ ...input });

    expect(second.wasNewlyCreated).toBe(false);
    expect(second.localTransaction.id).toBe(first.localTransaction.id);

    await handle.storage.close();
    handle.cleanup();
  });

  it("same key + DIFFERENT quantityKg throws IdempotencyKeyConflictError naming the field, and creates nothing", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "conflict-key-1", quantityKg: 40 });
    await handle.storage.createLocalTransaction(input);

    const conflictingInput = { ...input, quantityKg: 999 };
    await expect(handle.storage.createLocalTransaction(conflictingInput)).rejects.toThrow(IdempotencyKeyConflictError);

    try {
      await handle.storage.createLocalTransaction(conflictingInput);
      fail("expected IdempotencyKeyConflictError");
    } catch (err) {
      expect(err).toBeInstanceOf(IdempotencyKeyConflictError);
      const conflictErr = err as IdempotencyKeyConflictError;
      expect(conflictErr.conflictingFields).toEqual(["quantityKg"]);
      expect(conflictErr.localIdempotencyKey).toBe("conflict-key-1");
      expect(conflictErr.message).toContain("quantityKg");
    }

    // Still exactly one local_transaction for this key - the conflicting
    // attempt created NOTHING (not a second row, not a silently-accepted
    // overwrite of the first).
    const all = await handle.storage.listLocalTransactions();
    expect(all.filter((t) => t.localIdempotencyKey === "conflict-key-1")).toHaveLength(1);
    expect(all.find((t) => t.localIdempotencyKey === "conflict-key-1")?.quantityKg).toBe(40); // original value preserved, not overwritten

    await handle.storage.close();
    handle.cleanup();
  });

  it("reports every conflicting field, not just the first one found", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "conflict-key-multi", fat: 4.0, snf: 8.0, temperature: 4.0 });
    await handle.storage.createLocalTransaction(input);

    const conflictingInput = { ...input, fat: 5.0, snf: 9.0, temperature: 4.0 };
    try {
      await handle.storage.createLocalTransaction(conflictingInput);
      fail("expected IdempotencyKeyConflictError");
    } catch (err) {
      const conflictErr = err as IdempotencyKeyConflictError;
      expect(conflictErr.conflictingFields.sort()).toEqual(["fat", "snf"]);
    }

    await handle.storage.close();
    handle.cleanup();
  });

  it("a conflicting capturedAt alone is detected as a conflict, even if every numeric field matches", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "conflict-key-captured-at", capturedAt: "2026-01-01T00:00:00.000Z" });
    await handle.storage.createLocalTransaction(input);

    const conflictingInput = { ...input, capturedAt: "2026-01-01T00:05:00.000Z" };
    await expect(handle.storage.createLocalTransaction(conflictingInput)).rejects.toThrow(IdempotencyKeyConflictError);

    await handle.storage.close();
    handle.cleanup();
  });

  it("does NOT throw for centreId/sourceId/vehicleId/fat/snf/temperature matches with only quantityKg identical (sanity: no false positives)", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const input = sampleTransactionInput({ localIdempotencyKey: "no-false-positive-key" });
    await handle.storage.createLocalTransaction(input);

    // Genuinely identical payload (all fields equal) must NOT be flagged.
    await expect(handle.storage.createLocalTransaction({ ...input })).resolves.not.toThrow();

    await handle.storage.close();
    handle.cleanup();
  });
});
