/**
 * A Parser turns a raw byte frame from a Transport into a typed reading.
 * Framing/checksum/command-protocol details belong to a real
 * implementation of this interface for a specific device model - none
 * exists yet, per the project constraint against inventing RS232/Bluetooth
 * protocols. Checkpoint 4's simulator devices produce already-structured
 * readings directly and so don't exercise a byte-parsing Parser at all;
 * this interface exists now so the pipeline shape
 * (Device -> Transport -> Parser -> NormalizedReading) is fixed before any
 * real parser is written against it.
 */
export interface Parser<TReading> {
  /** Returns the parsed reading, or null if `frame` is not a complete/valid frame yet. */
  parse(frame: Buffer): TReading | null;
}
