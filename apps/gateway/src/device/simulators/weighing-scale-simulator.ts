import type { ScaleReading } from "../../normalization/normalized-reading.types";
import type { DeviceKind } from "../device.types";
import { SimulatedDeviceBase } from "./simulated-device-base";
import { createSeededRandom, randomInRange } from "./seeded-random";

/**
 * Simulated weighing scale. Concrete Device<ScaleReading> implementation -
 * see SimulatedDeviceBase for the shared connection-state-machine +
 * scriptable-behavior mechanics this builds on.
 */
export class WeighingScaleSimulator extends SimulatedDeviceBase<ScaleReading> {
  readonly kind: DeviceKind = "SCALE";

  /**
   * Optional, explicitly-opted-into helper for demos: queues a
   * plausible-looking (not BRD-confirmed) reading using the seeded PRNG
   * rather than a fixed value. NEVER called by test code exercising core
   * behavior - tests always use setDefaultReading()/setNextReadings()
   * with exact fixed values, per the "do not introduce randomness into
   * core tests" instruction. `seed` must be supplied by the caller
   * (no implicit default) so the caller is always explicitly opting into
   * randomness, not accidentally getting it.
   */
  simulateRealisticReading(seed: number, capturedAt: string): void {
    const rng = createSeededRandom(seed);
    const weightKg = Math.round(randomInRange(rng, 200, 8000) * 100) / 100;
    this.setNextReadings([{ weightKg, stable: true, capturedAt }]);
  }
}
