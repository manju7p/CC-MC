import type { GatewayConfig } from "../config/config.types";
import type { ConnectivityState, GatewayHealthSnapshot, GatewayServiceState } from "./health.types";

/**
 * Tracks and reports the gateway's own lifecycle/health state. This is
 * intentionally a plain in-memory tracker with no persistence - health
 * state is a live runtime fact ("what is this process doing right now"),
 * not a durable record, so restart-recovery concerns (Checkpoint 3) don't
 * apply here the way they do to transactions/outbox rows.
 */
export class HealthService {
  private serviceState: GatewayServiceState = "STOPPED";
  private startedAtMs: number | null = null;

  // Checkpoint 2 placeholders - see health.types.ts for why these are
  // fixed at UNKNOWN/0 until later checkpoints wire real subsystems in.
  private cloudConnectivity: ConnectivityState = "UNKNOWN";
  private deviceConnectivity: ConnectivityState = "UNKNOWN";
  private pendingSyncCount = 0;

  constructor(
    private readonly config: GatewayConfig,
    private readonly version: string,
    private readonly now: () => number = Date.now,
  ) {}

  getServiceState(): GatewayServiceState {
    return this.serviceState;
  }

  setServiceState(state: GatewayServiceState): void {
    this.serviceState = state;
  }

  /** Marks the moment the gateway finished starting; uptime is measured from here. */
  markStarted(): void {
    this.startedAtMs = this.now();
  }

  /** Clears the started-at marker, e.g. on a full stop. Uptime reads as 0 again after this. */
  clearStarted(): void {
    this.startedAtMs = null;
  }

  setCloudConnectivity(state: ConnectivityState): void {
    this.cloudConnectivity = state;
  }

  setDeviceConnectivity(state: ConnectivityState): void {
    this.deviceConnectivity = state;
  }

  setPendingSyncCount(count: number): void {
    this.pendingSyncCount = count;
  }

  getSnapshot(): GatewayHealthSnapshot {
    const uptimeSeconds = this.startedAtMs === null ? 0 : Math.floor((this.now() - this.startedAtMs) / 1000);

    return {
      gatewayId: this.config.gatewayId,
      centreId: this.config.centreId,
      version: this.version,
      startedAt: this.startedAtMs === null ? null : new Date(this.startedAtMs).toISOString(),
      uptimeSeconds,
      serviceState: this.serviceState,
      cloudConnectivity: this.cloudConnectivity,
      deviceConnectivity: this.deviceConnectivity,
      pendingSyncCount: this.pendingSyncCount,
    };
  }
}
