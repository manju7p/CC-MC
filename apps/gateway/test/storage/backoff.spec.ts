import { computeBackoffDelayMs, DEFAULT_BACKOFF_OPTIONS } from "../../src/storage/backoff";

describe("computeBackoffDelayMs", () => {
  it("returns baseDelayMs for the first retry (attemptCount=1)", () => {
    expect(computeBackoffDelayMs(1, { baseDelayMs: 1000, maxDelayMs: 60_000 })).toBe(1000);
  });

  it("doubles per attempt", () => {
    const opts = { baseDelayMs: 1000, maxDelayMs: 1_000_000 };
    expect(computeBackoffDelayMs(1, opts)).toBe(1000);
    expect(computeBackoffDelayMs(2, opts)).toBe(2000);
    expect(computeBackoffDelayMs(3, opts)).toBe(4000);
    expect(computeBackoffDelayMs(4, opts)).toBe(8000);
  });

  it("caps at maxDelayMs", () => {
    const opts = { baseDelayMs: 1000, maxDelayMs: 5000 };
    expect(computeBackoffDelayMs(10, opts)).toBe(5000);
    expect(computeBackoffDelayMs(100, opts)).toBe(5000);
  });

  it("is a pure function - same input always produces the same output (deterministic, no jitter)", () => {
    const a = computeBackoffDelayMs(3, DEFAULT_BACKOFF_OPTIONS);
    const b = computeBackoffDelayMs(3, DEFAULT_BACKOFF_OPTIONS);
    expect(a).toBe(b);
  });

  it("throws for attemptCount < 1", () => {
    expect(() => computeBackoffDelayMs(0)).toThrow();
    expect(() => computeBackoffDelayMs(-1)).toThrow();
  });

  it("default options: 1s base, 5 minute cap", () => {
    expect(DEFAULT_BACKOFF_OPTIONS.baseDelayMs).toBe(1_000);
    expect(DEFAULT_BACKOFF_OPTIONS.maxDelayMs).toBe(5 * 60_000);
  });
});
