import { Injectable } from "@nestjs/common";
import { QualityParameter, TransactionStatus } from "@cc-mc/shared-types";

export interface QualityReadingInput {
  fat: number;
  snf: number;
  temperature: number;
}

export interface ResolvedRule {
  minValue: number;
  maxValue: number;
}

export interface QualityValidationResult {
  status: TransactionStatus.ACCEPTED | TransactionStatus.HOLD;
  reason: string | null;
}

/**
 * Isolated quality-validation business logic (kept out of the controller
 * and out of ReceptionService's persistence code, per the project's
 * "Quality Validation" instruction: `validateQuality(reading, rules) ->
 * ACCEPT/REJECT/HOLD`).
 *
 * MVP interpretation of BRD Sections 13 & 38 (documented in
 * docs/assumptions.md #auto-reject-vs-hold): the BRD's own diagram shows
 * an out-of-limit reading going to HOLD, then a Manager resolving it to
 * ACCEPT or REJECT. Nothing in the BRD describes the system ever
 * auto-rejecting on its own. So this function only ever returns ACCEPTED
 * or HOLD - REJECTED is reachable exclusively through a Manager override
 * of a HOLD (see ReceptionService.override). This is a documented
 * assumption, not an invented protocol detail.
 */
@Injectable()
export class QualityValidationService {
  validate(reading: QualityReadingInput, rules: Map<QualityParameter, ResolvedRule>): QualityValidationResult {
    const failures: string[] = [];

    const checks: Array<[QualityParameter, number]> = [
      [QualityParameter.FAT, reading.fat],
      [QualityParameter.SNF, reading.snf],
      [QualityParameter.TEMPERATURE, reading.temperature],
    ];

    for (const [param, value] of checks) {
      const rule = rules.get(param);
      if (!rule) {
        // ReceptionService is responsible for resolving all required rules
        // before calling validate(); a missing rule here is a programming
        // error, not a business outcome.
        throw new Error(`No resolved rule provided for parameter ${param}`);
      }
      if (value < rule.minValue || value > rule.maxValue) {
        failures.push(`${param} (${value}) outside configured range [${rule.minValue}, ${rule.maxValue}]`);
      }
    }

    if (failures.length === 0) {
      return { status: TransactionStatus.ACCEPTED, reason: null };
    }

    return {
      status: TransactionStatus.HOLD,
      reason: `Quality outside limits: ${failures.join("; ")}`,
    };
  }
}
