import { QualityParameter, TransactionStatus } from "@cc-mc/shared-types";
import { QualityValidationService } from "./quality-validation.service";

describe("QualityValidationService", () => {
  const service = new QualityValidationService();

  const rules = new Map([
    [QualityParameter.FAT, { minValue: 3.0, maxValue: 6.0 }],
    [QualityParameter.SNF, { minValue: 8.0, maxValue: 10.0 }],
    [QualityParameter.TEMPERATURE, { minValue: 0, maxValue: 10.0 }],
  ]);

  it("accepts a reading where every parameter is within range", () => {
    const result = service.validate({ fat: 4.2, snf: 8.5, temperature: 7.2 }, rules);
    expect(result.status).toBe(TransactionStatus.ACCEPTED);
    expect(result.reason).toBeNull();
  });

  it("accepts a reading exactly on the boundary (inclusive range)", () => {
    const result = service.validate({ fat: 3.0, snf: 10.0, temperature: 0 }, rules);
    expect(result.status).toBe(TransactionStatus.ACCEPTED);
  });

  it("holds a reading with FAT below the minimum", () => {
    const result = service.validate({ fat: 2.0, snf: 8.5, temperature: 7.2 }, rules);
    expect(result.status).toBe(TransactionStatus.HOLD);
    expect(result.reason).toContain("FAT");
  });

  it("holds a reading with multiple parameters out of range and lists all of them", () => {
    const result = service.validate({ fat: 2.0, snf: 12.0, temperature: 7.2 }, rules);
    expect(result.status).toBe(TransactionStatus.HOLD);
    expect(result.reason).toContain("FAT");
    expect(result.reason).toContain("SNF");
  });

  it("never returns REJECTED directly - only ACCEPTED or HOLD (see docs/assumptions.md #auto-reject-vs-hold)", () => {
    const result = service.validate({ fat: 0, snf: 0, temperature: 999 }, rules);
    expect([TransactionStatus.ACCEPTED, TransactionStatus.HOLD]).toContain(result.status);
  });

  it("throws if a required rule was not resolved by the caller", () => {
    const incompleteRules = new Map([[QualityParameter.FAT, { minValue: 3.0, maxValue: 6.0 }]]);
    expect(() => service.validate({ fat: 4.2, snf: 8.5, temperature: 7.2 }, incompleteRules)).toThrow();
  });
});
