/**
 * Normalized reading shapes - what a Device produces after its
 * device-specific Parser has run, and what eventually gets persisted
 * (Checkpoint 3) and synced to the cloud reception API (Checkpoint 5).
 *
 * Every field below is explicitly labeled as either BRD-defined (the
 * business requirements actually specify this field) or ASSUMED
 * (introduced for the Checkpoint 4 simulator's own bookkeeping, with no
 * BRD or hardware backing) so nothing here is silently mistaken for a
 * confirmed requirement. See Rule 12 ("every architectural assumption
 * must be documented").
 */

export interface ScaleReading {
  /** BRD-defined: MilkReceptionTransaction.quantityKg is the cloud field this maps to. */
  weightKg: number;

  /**
   * ASSUMED simulator field - not BRD-defined, not hardware-confirmed.
   * Real weighing scales commonly report a "stable/settled" flag once the
   * reading has stopped fluctuating, so the Checkpoint 4 simulator
   * produces one to make the simulated flow realistic, but no real scale's
   * actual protocol has been seen yet - this may not match whatever
   * hardware is eventually integrated.
   */
  stable: boolean;

  /** When this reading was captured, ISO-8601. */
  capturedAt: string;
}

export interface AnalyserReading {
  /** BRD-defined (BRD Section 37 / QualityParameter.FAT). */
  fat: number;

  /** BRD-defined (BRD Section 37 / QualityParameter.SNF). */
  snf: number;

  /** BRD-defined (BRD Section 37 / QualityParameter.TEMPERATURE). */
  temperature: number;

  /**
   * Reserved for BRD Section 23's additional named parameters (CLR,
   * Density, etc.) that the MVP's quality validation does not yet act on.
   * Not populated by the Checkpoint 4 simulator; present so the shape
   * doesn't need to change again once those parameters are wired up.
   */
  extraParameters?: Record<string, number>;

  /** When this reading was captured, ISO-8601. */
  capturedAt: string;
}

export type NormalizedReading =
  | { kind: "SCALE"; reading: ScaleReading }
  | { kind: "ANALYSER"; reading: AnalyserReading };
