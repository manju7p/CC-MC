import { assembleReception, InvalidReadingError } from "../../src/capture/reception-validation";
import type { AnalyserReading, ScaleReading } from "../../src/normalization/normalized-reading.types";

function validScale(overrides: Partial<ScaleReading> = {}): ScaleReading {
  return { weightKg: 500, stable: true, capturedAt: "2026-01-01T00:00:00.000Z", ...overrides };
}

function validAnalyser(overrides: Partial<AnalyserReading> = {}): AnalyserReading {
  return { fat: 4.2, snf: 8.5, temperature: 4.0, capturedAt: "2026-01-01T00:00:01.000Z", ...overrides };
}

/** Runs a throwing function and returns what it threw, instead of letting it propagate - avoids relying on jest-circus's removed global fail(). */
function captureThrown(fn: () => void): unknown {
  try {
    fn();
    throw new Error("expected fn() to throw, but it did not");
  } catch (err) {
    return err;
  }
}

describe("assembleReception", () => {
  it("assembles a valid scale + analyser reading into a complete AssembledReception, using assembledAt (not either device's own capturedAt)", () => {
    const result = assembleReception({
      centreId: 1,
      sourceId: 2,
      vehicleId: 3,
      scaleReading: validScale({ weightKg: 480.25 }),
      analyserReading: validAnalyser({ fat: 4.4, snf: 8.7, temperature: 3.9 }),
      assembledAt: "2026-01-01T00:00:05.000Z",
    });

    expect(result).toEqual({
      centreId: 1,
      sourceId: 2,
      vehicleId: 3,
      quantityKg: 480.25,
      fat: 4.4,
      snf: 8.7,
      temperature: 3.9,
      capturedAt: "2026-01-01T00:00:05.000Z",
    });
  });

  it("temperature has no minimum - a negative temperature is valid (mirrors the DTO's lack of @Min on temperature)", () => {
    const result = assembleReception({
      centreId: 1,
      sourceId: 1,
      vehicleId: 1,
      scaleReading: validScale(),
      analyserReading: validAnalyser({ temperature: -2.5 }),
      assembledAt: "2026-01-01T00:00:00.000Z",
    });

    expect(result.temperature).toBe(-2.5);
  });

  it.each([
    ["centreId", { centreId: 1.5 }],
    ["sourceId", { sourceId: 1.5 }],
    ["vehicleId", { vehicleId: 1.5 }],
  ] as const)("rejects a non-integer %s", (field, overrides) => {
    expect(() =>
      assembleReception({
        centreId: 1,
        sourceId: 1,
        vehicleId: 1,
        ...overrides,
        scaleReading: validScale(),
        analyserReading: validAnalyser(),
        assembledAt: "2026-01-01T00:00:00.000Z",
      }),
    ).toThrow(InvalidReadingError);
  });

  it("rejects a negative quantityKg (scale weightKg < 0)", () => {
    const err = captureThrown(() =>
      assembleReception({
        centreId: 1,
        sourceId: 1,
        vehicleId: 1,
        scaleReading: validScale({ weightKg: -10 }),
        analyserReading: validAnalyser(),
        assembledAt: "2026-01-01T00:00:00.000Z",
      }),
    );

    expect(err).toBeInstanceOf(InvalidReadingError);
    const violations = (err as InvalidReadingError).violations;
    expect(violations).toEqual([{ field: "quantityKg", message: expect.stringContaining("-10") }]);
  });

  it("rejects negative fat and negative snf", () => {
    const err = captureThrown(() =>
      assembleReception({
        centreId: 1,
        sourceId: 1,
        vehicleId: 1,
        scaleReading: validScale(),
        analyserReading: validAnalyser({ fat: -1, snf: -2 }),
        assembledAt: "2026-01-01T00:00:00.000Z",
      }),
    );

    expect(err).toBeInstanceOf(InvalidReadingError);
    const fields = (err as InvalidReadingError).violations.map((v) => v.field);
    expect(fields.sort()).toEqual(["fat", "snf"]);
  });

  it("reports EVERY violation at once, not just the first", () => {
    const err = captureThrown(() =>
      assembleReception({
        centreId: 1.1,
        sourceId: 1,
        vehicleId: 1,
        scaleReading: validScale({ weightKg: -5 }),
        analyserReading: validAnalyser({ fat: -1, snf: -1, temperature: Number.NaN }),
        assembledAt: "2026-01-01T00:00:00.000Z",
      }),
    );

    expect(err).toBeInstanceOf(InvalidReadingError);
    const fields = (err as InvalidReadingError).violations.map((v) => v.field).sort();
    expect(fields).toEqual(["centreId", "fat", "quantityKg", "snf", "temperature"]);
  });

  it("rejects non-finite values (NaN/Infinity)", () => {
    expect(() =>
      assembleReception({
        centreId: 1,
        sourceId: 1,
        vehicleId: 1,
        scaleReading: validScale({ weightKg: Number.POSITIVE_INFINITY }),
        analyserReading: validAnalyser(),
        assembledAt: "2026-01-01T00:00:00.000Z",
      }),
    ).toThrow(InvalidReadingError);
  });
});
