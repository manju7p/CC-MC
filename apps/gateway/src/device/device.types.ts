/** The two device categories this gateway integrates, per the BRD. */
export type DeviceKind = "SCALE" | "ANALYSER";

export type DeviceConnectionState = "DISCONNECTED" | "CONNECTING" | "CONNECTED" | "ERROR";

export interface DeviceStatus {
  state: DeviceConnectionState;
  /** Present only when state is "ERROR". Human-readable, not a protocol-level code. */
  lastError?: string;
}

/**
 * A Device is the gateway's abstraction over a single physical instrument
 * (one weighing scale, one milk analyser). It is generic over its own
 * reading type so a ScaleDevice returns ScaleReading and an
 * AnalyserDevice returns AnalyserReading without either needing a cast.
 *
 * Checkpoint 4 adds the first concrete implementations:
 * WeighingScaleSimulator and MilkAnalyserSimulator
 * (device/simulators/*.ts), which implement this interface DIRECTLY -
 * they do not go through a Transport/Parser. That is a deliberate design
 * decision, not a shortcut: Transport/Parser (transport/transport.types.ts,
 * parser/parser.types.ts) exist specifically as the seam for a future
 * byte-level protocol (RS232, Bluetooth) - something that receives raw
 * bytes and has to frame/parse them into a reading. A simulator has no
 * raw byte stream to parse; it generates an already-structured
 * ScaleReading/AnalyserReading programmatically. Routing that through a
 * fake "SimulatorTransport" + "SimulatorParser" would mean inventing an
 * arbitrary byte encoding for data that is never actually transmitted as
 * bytes - pure ceremony with no real seam behind it, and exactly the kind
 * of invented protocol detail the project's strict rules warn against.
 * The FIRST real implementation to actually need Transport/Parser will be
 * a real hardware adapter (blocked on hardware specs - see
 * docs/gateway-architecture.md), and it will implement this same Device
 * interface, so the capture/application layer above Device never has to
 * know or care whether a given device is simulated or real hardware.
 */
export interface Device<TReading> {
  readonly kind: DeviceKind;
  readonly deviceId: string;

  connect(): Promise<void>;
  disconnect(): Promise<void>;
  status(): DeviceStatus;

  /**
   * Takes one reading from the device. Rejects if the device is not
   * connected. What "taking a reading" means physically (polling vs.
   * waiting for a pushed frame) is left to the concrete implementation -
   * this interface only fixes the request/response shape.
   */
  read(): Promise<TReading>;
}
