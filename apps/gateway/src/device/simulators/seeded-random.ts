/**
 * A tiny, deterministic pseudo-random number generator (mulberry32), used
 * ONLY for optional, explicitly-opted-into "realistic-looking demo data"
 * in the simulators (e.g. simulateRealisticReading()). Never used by any
 * default code path, and NEVER used by anything under test/ - core tests
 * always script exact, fixed values (setNextReadings/setDefaultReading),
 * per the instruction "Do not introduce randomness into core tests. If
 * randomness is useful for demos, make it explicitly optional and
 * seedable."
 *
 * Deliberately not Math.random(): a seed must be reproducible run to run
 * (given the same seed, the same sequence of "random" values comes out
 * every time), which Math.random() cannot offer. mulberry32 is a small,
 * well-known, public-domain 32-bit PRNG - good enough for "vary demo
 * numbers a bit," not intended or needed for anything security-sensitive.
 */
export function createSeededRandom(seed: number): () => number {
  let state = seed >>> 0;
  return function next(): number {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Returns a float in [min, max), using the given seeded generator. */
export function randomInRange(rng: () => number, min: number, max: number): number {
  return min + rng() * (max - min);
}
