import type { Device } from "./device.types";
import type { Logger } from "../logging/logger";

/**
 * Tracks the set of devices this gateway instance knows about and manages
 * their connect/disconnect lifecycle as a group. Starts with zero
 * registered devices in Checkpoint 2 - nothing calls register() yet, since
 * no Device implementations exist until Checkpoint 4's simulators.
 *
 * Kept deliberately dumb: no polling loop, no read scheduling, no
 * reconnect/backoff logic. Those are Checkpoint 4+ concerns once there is
 * an actual device to observe failing.
 */
export class DeviceManager {
  private readonly devices = new Map<string, Device<unknown>>();

  constructor(private readonly logger: Logger) {}

  register(device: Device<unknown>): void {
    if (this.devices.has(device.deviceId)) {
      throw new Error(`Device with id "${device.deviceId}" is already registered.`);
    }
    this.devices.set(device.deviceId, device);
    this.logger.info("Device registered", { deviceId: device.deviceId, kind: device.kind });
  }

  unregister(deviceId: string): void {
    this.devices.delete(deviceId);
    this.logger.info("Device unregistered", { deviceId });
  }

  list(): Device<unknown>[] {
    return Array.from(this.devices.values());
  }

  async connectAll(): Promise<void> {
    for (const device of this.devices.values()) {
      await device.connect();
    }
  }

  async disconnectAll(): Promise<void> {
    for (const device of this.devices.values()) {
      await device.disconnect();
    }
  }
}
