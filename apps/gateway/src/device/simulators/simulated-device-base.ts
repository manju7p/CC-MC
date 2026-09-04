import type { Device, DeviceConnectionState, DeviceKind, DeviceStatus } from "../device.types";

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

type QueuedOutcome<TReading> = { kind: "value"; reading: TReading } | { kind: "error"; message: string };

/**
 * Shared connection-state-machine + scriptable-behavior implementation for
 * both simulator devices (WeighingScaleSimulator, MilkAnalyserSimulator).
 * Behaves like an actual device - it has real connect/disconnect state,
 * rejects read() when not connected, and its readings come from an
 * explicitly configured queue/default rather than a single hardcoded
 * return value - not "simply return a hardcoded object from one
 * function," per the checkpoint's instruction.
 *
 * Supports every scenario the checkpoint asks for as a minimum:
 *  - normal reading: setDefaultReading() or setNextReadings()
 *  - repeated reading: setDefaultReading() repeats indefinitely once the
 *    scripted queue (if any) is exhausted
 *  - invalid reading: queue a reading with a semantically bad value (e.g.
 *    negative weight) via setNextReadings() - the simulator does NOT
 *    validate its own readings (that is the capture/assembly layer's job,
 *    see capture/reception-validation.ts and its "Separate: raw device
 *    communication -> normalized reading -> reception transaction"
 *    reasoning), so this device happily returns exactly what it's told to
 * -  device unavailable: simulateConnectFailure() (next connect() fails)
 *    or goOffline() (forces the CURRENT connection into ERROR state,
 *    simulating a device that was connected and then dropped out)
 *  - delayed response: simulateNextReadDelay()
 *
 * Deliberately does NOT implement any framing/parsing/protocol - there is
 * no Transport/Parser involved (see device.types.ts's updated header
 * comment for why that is the correct design, not a shortcut).
 */
export abstract class SimulatedDeviceBase<TReading> implements Device<TReading> {
  abstract readonly kind: DeviceKind;
  readonly deviceId: string;

  private connectionState: DeviceConnectionState = "DISCONNECTED";
  private lastError: string | undefined;
  private pendingConnectFailureMessage: string | null = null;
  private readonly outcomeQueue: QueuedOutcome<TReading>[] = [];
  private defaultReading: TReading | null = null;
  private pendingReadDelayMs = 0;

  constructor(deviceId: string) {
    this.deviceId = deviceId;
  }

  // --- Device interface ---

  async connect(): Promise<void> {
    if (this.pendingConnectFailureMessage !== null) {
      const message = this.pendingConnectFailureMessage;
      // One-shot: a subsequent connect() call can succeed, simulating the
      // device coming back online rather than being permanently broken.
      this.pendingConnectFailureMessage = null;
      this.connectionState = "ERROR";
      this.lastError = message;
      throw new Error(message);
    }
    this.connectionState = "CONNECTED";
    this.lastError = undefined;
  }

  async disconnect(): Promise<void> {
    this.connectionState = "DISCONNECTED";
    this.lastError = undefined;
  }

  status(): DeviceStatus {
    return this.lastError === undefined ? { state: this.connectionState } : { state: this.connectionState, lastError: this.lastError };
  }

  async read(): Promise<TReading> {
    if (this.connectionState !== "CONNECTED") {
      throw new Error(
        `${this.kind} device "${this.deviceId}" is not connected (state=${this.connectionState}) - cannot read. ` +
          `Call connect() first.`,
      );
    }

    if (this.pendingReadDelayMs > 0) {
      const delay = this.pendingReadDelayMs;
      this.pendingReadDelayMs = 0;
      await sleep(delay);
    }

    const next = this.outcomeQueue.shift();
    if (next) {
      if (next.kind === "error") {
        throw new Error(next.message);
      }
      return next.reading;
    }

    if (this.defaultReading !== null) {
      return this.defaultReading;
    }

    throw new Error(
      `${this.kind} device "${this.deviceId}" has no reading configured - call setDefaultReading() or setNextReadings() first.`,
    );
  }

  // --- Scripting / configuration API (used by tests and demos) ---

  /** Sets the reading read() returns once the scripted queue (if any) is exhausted - repeats indefinitely ("repeated reading"). */
  setDefaultReading(reading: TReading): void {
    this.defaultReading = reading;
  }

  /** Queues readings consumed one per read() call, in order, before falling back to the default reading. */
  setNextReadings(readings: TReading[]): void {
    for (const reading of readings) {
      this.outcomeQueue.push({ kind: "value", reading });
    }
  }

  /** Queues a single read() call that rejects with this message (a sensor fault WHILE still connected - distinct from being disconnected). */
  queueReadFailure(message: string): void {
    this.outcomeQueue.push({ kind: "error", message });
  }

  /** The NEXT connect() call will fail with this message (device unavailable at connect time). One-shot. */
  simulateConnectFailure(message: string): void {
    this.pendingConnectFailureMessage = message;
  }

  /** Forces the device into ERROR state immediately, regardless of current state - simulates a connected device going unavailable. */
  goOffline(message: string): void {
    this.connectionState = "ERROR";
    this.lastError = message;
  }

  /** The NEXT read() call (only) waits this many milliseconds before resolving/rejecting - simulates a delayed response. */
  simulateNextReadDelay(delayMs: number): void {
    this.pendingReadDelayMs = delayMs;
  }
}
