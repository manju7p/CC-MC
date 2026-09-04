import type { AnalyserReading } from "../../normalization/normalized-reading.types";
import type { DeviceKind } from "../device.types";
import { SimulatedDeviceBase } from "./simulated-device-base";
import { createSeededRandom, randomInRange } from "./seeded-random";

/**
 * Simulated milk analyser. Concrete Device<AnalyserReading>
 * implementation - see SimulatedDeviceBase for the shared mechanics.
 */
export class MilkAnalyserSimulator extends SimulatedDeviceBase<AnalyserReading> {
  readonly kind: DeviceKind = "ANALYSER";

  /**
   * Optional, seedable demo-only helper - see
   * WeighingScaleSimulator.simulateRealisticReading()'s doc comment for
   * why this is never used by core tests. Ranges are plausible-looking
   * only, not BRD-confirmed or hardware-confirmed values.
   */
  simulateRealisticReading(seed: number, capturedAt: string): void {
    const rng = createSeededRandom(seed);
    const fat = Math.round(randomInRange(rng, 3, 6) * 100) / 100;
    const snf = Math.round(randomInRange(rng, 8, 9.5) * 100) / 100;
    const temperature = Math.round(randomInRange(rng, 2, 8) * 100) / 100;
    this.setNextReadings([{ fat, snf, temperature, capturedAt }]);
  }
}
