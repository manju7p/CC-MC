import type { AnalyserReading, ScaleReading } from "../normalization/normalized-reading.types";

/**
 * Thrown by assembleReception() when the assembled reception fails
 * validation. Lists EVERY violation found, not just the first - so a
 * caller sees the full picture in one shot rather than fixing one field
 * at a time. `field` names match the property names on
 * AssembledReception/CreateLocalTransactionInput (e.g. "quantityKg", not
 * the device-level "weightKg"), since this is validating the assembled
 * reception, not the raw device reading.
 */
export class InvalidReadingError extends Error {
  constructor(public readonly violations: ReceptionValidationViolation[]) {
    super(
      `Assembled reception failed validation (${violations.length} violation(s)): ` +
        violations.map((v) => `${v.field}: ${v.message}`).join("; "),
    );
    this.name = "InvalidReadingError";
  }
}

export interface ReceptionValidationViolation {
  field: string;
  message: string;
}

export interface AssembleReceptionInput {
  centreId: number;
  sourceId: number;
  vehicleId: number;
  scaleReading: ScaleReading;
  analyserReading: AnalyserReading;
  /**
   * When this reception was assembled (wall-clock at the moment the
   * capture workflow combined the scale + analyser readings) - see
   * ReceptionCaptureWorkflow's doc comment for why this, rather than
   * either device reading's own capturedAt, is what gets stored as the
   * LocalTransaction's capturedAt. Passed in (not read via Date.now()
   * here) so this function stays a pure, easily-testable function.
   */
  assembledAt: string;
}

export interface AssembledReception {
  centreId: number;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
  capturedAt: string;
}

/**
 * Validates and assembles a scale reading + an analyser reading into the
 * shape LocalStorage.createLocalTransaction() accepts (minus
 * localIdempotencyKey, which the capture workflow attaches separately).
 *
 * Validation rules are a DELIBERATE, field-by-field mirror of
 * apps/api/src/reception/dto/create-reception.dto.ts's class-validator
 * decorators - not stricter, not looser, per the instruction "assembleReception()
 * ... mirroring CreateReceptionDto exactly". Specifically:
 *   - centreId/sourceId/vehicleId: must be integers (IsInt)
 *   - quantityKg/fat/snf: must be numbers >= 0 (IsNumber + Min(0))
 *   - temperature: must be a number, NO minimum (IsNumber only - the DTO
 *     deliberately has no @Min() here, since milk temperature readings
 *     can be negative in some ambient/refrigerated conditions; this
 *     gateway does not invent a stricter rule the cloud API doesn't
 *     enforce)
 * The gateway does not use class-validator/class-transformer itself
 * (no NestJS-style dependency here - Rule 11, boring/minimal deps for an
 * edge process); this is a small hand-written equivalent of the same
 * rules, kept in sync by this comment pointing at the DTO as the source
 * of truth.
 *
 * Throws InvalidReadingError (never returns a partial/invalid result) so
 * an invalid assembled reception can never reach
 * LocalStorage.createLocalTransaction() - satisfying failure semantics
 * requirement #2 ("Device returns malformed/invalid normalized data ->
 * NO invalid transaction should enter SQLite").
 */
export function assembleReception(input: AssembleReceptionInput): AssembledReception {
  const violations: ReceptionValidationViolation[] = [];

  const requireInt = (field: string, value: number): void => {
    if (!Number.isFinite(value) || !Number.isInteger(value)) {
      violations.push({ field, message: `must be an integer, got ${JSON.stringify(value)}` });
    }
  };

  const requireNumberMinZero = (field: string, value: number): void => {
    if (!Number.isFinite(value)) {
      violations.push({ field, message: `must be a number, got ${JSON.stringify(value)}` });
      return;
    }
    if (value < 0) {
      violations.push({ field, message: `must be >= 0, got ${value}` });
    }
  };

  const requireNumber = (field: string, value: number): void => {
    if (!Number.isFinite(value)) {
      violations.push({ field, message: `must be a number, got ${JSON.stringify(value)}` });
    }
  };

  requireInt("centreId", input.centreId);
  requireInt("sourceId", input.sourceId);
  requireInt("vehicleId", input.vehicleId);
  requireNumberMinZero("quantityKg", input.scaleReading.weightKg);
  requireNumberMinZero("fat", input.analyserReading.fat);
  requireNumberMinZero("snf", input.analyserReading.snf);
  // Deliberately requireNumber, not requireNumberMinZero - see doc comment above.
  requireNumber("temperature", input.analyserReading.temperature);

  if (violations.length > 0) {
    throw new InvalidReadingError(violations);
  }

  return {
    centreId: input.centreId,
    sourceId: input.sourceId,
    vehicleId: input.vehicleId,
    quantityKg: input.scaleReading.weightKg,
    fat: input.analyserReading.fat,
    snf: input.analyserReading.snf,
    temperature: input.analyserReading.temperature,
    capturedAt: input.assembledAt,
  };
}
