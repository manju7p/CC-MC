/**
 * A Transport is the raw byte-level channel to a physical device - serial
 * port, Bluetooth socket, TCP socket, or (Checkpoint 4) an in-process
 * simulator. This interface is deliberately protocol-agnostic: it knows
 * nothing about RS232 framing, Bluetooth GATT profiles, baud rates, or any
 * other hardware-specific detail.
 *
 * This is the seam a future RS232Transport or BluetoothTransport plugs
 * into without any code above this layer (Device, Parser, normalization,
 * storage, sync) needing to change. Per the explicit project constraints,
 * NO real transport implementation exists yet - real device protocol
 * details (baud rate, parity, packet format, Bluetooth profile) are not
 * yet known and must not be invented. Checkpoint 4 adds a SimulatorTransport
 * that implements this same interface without touching any real hardware
 * protocol.
 */
export interface Transport {
  /** Opens the underlying channel. Resolves once the channel is ready to read/write. */
  open(): Promise<void>;

  /** Closes the underlying channel. Safe to call even if already closed. */
  close(): Promise<void>;

  /** Whether the channel is currently open. */
  isOpen(): boolean;

  /**
   * Registers a handler invoked with each raw chunk of data received from
   * the device. What a "chunk" means (a full frame vs. a partial buffer)
   * is transport-specific; framing/parsing is the Parser's job, not the
   * Transport's.
   */
  onData(handler: (chunk: Buffer) => void): void;
}
