import { SerialPort } from "serialport";
import type { Transport } from "./transport.types";
import type { SerialTransportConfig } from "./serial-transport.types";
import { ESSAE_CONFIRMED_SERIAL_PARAMETERS } from "./serial-transport.types";
import type { Logger } from "../logging/logger";

/**
 * A `SerialPort`-shaped constructor - `SerialPort` itself and
 * `SerialPortMock` (from the same `serialport` package, used in tests -
 * see `test/transport/serial-transport.spec.ts`) both satisfy this shape.
 * Injected so this class never has to know which one it's talking to.
 */
type SerialPortLike = Pick<SerialPort, "open" | "close" | "isOpen" | "on" | "removeAllListeners">;
type SerialPortCtor = new (options: {
  path: string;
  baudRate: number;
  dataBits: 5 | 6 | 7 | 8;
  parity: "none" | "even" | "odd" | "mark" | "space";
  stopBits: 1 | 1.5 | 2;
  rtscts: boolean;
  xon: boolean;
  xoff: boolean;
  xany: boolean;
  autoOpen: boolean;
}) => SerialPortLike;

/**
 * Generic RS232 serial transport - implements ONLY the byte-level
 * `Transport` interface (`transport.types.ts`): open/close/isOpen and a
 * raw-chunk data callback. It has NO knowledge of the ESSAE message
 * protocol running on top of this wire (packet format, framing,
 * checksums, commands, field positions) - that specification has not been
 * supplied and is deliberately NOT implemented here or anywhere in this
 * repository. See `../parser/parser.types.ts` for that boundary and
 * `docs/gateway-architecture.md` §15 for the full reasoning.
 *
 * What IS implemented, and is real: opening a genuine OS serial port at a
 * configurable baud rate/path, with the four other RS232 parameters fixed
 * to the CEO-confirmed values for the ESSAE equipment
 * (`ESSAE_CONFIRMED_SERIAL_PARAMETERS`) - 8 data bits, no parity, 1 stop
 * bit, no flow control.
 *
 * A trade-off flagged deliberately, not silently accepted: this uses the
 * `serialport` npm package, whose actual byte I/O is a compiled native
 * addon (`@serialport/bindings-cpp`). That reintroduces exactly the
 * native-addon/vendored-runtime-ABI risk this project deliberately AVOIDED
 * for SQLite (`node:sqlite` over `better-sqlite3` - see
 * `docs/gateway-architecture.md` §2) and HTTP (built-in `fetch` over
 * `axios`/`node-fetch`): the addon's prebuilt binary must match the exact
 * platform/arch/Node-ABI of whatever `node.exe` ends up vendored into the
 * WinSW deployment (`docs/gateway-decision.md`). Unlike SQLite/HTTP,
 * there is no built-in Node.js serial port API, so this trade-off could
 * not be avoided the same way - it is accepted here because there is no
 * alternative, not because the risk is smaller. This has been verified to
 * install and load in this Linux sandbox (a prebuilt binary was available
 * for linux-x64); it has NOT been verified against the actual vendored
 * Windows `node.exe` referenced in `apps/gateway/packaging/build-deployment.mjs`,
 * which is itself still a placeholder file as of Checkpoint 6 (see that
 * script's own header comment) - so this is genuinely unverified for the
 * real deployment target, not just untested on real hardware.
 */
export class SerialTransport implements Transport {
  private readonly port: SerialPortLike;
  private dataHandler: ((chunk: Buffer) => void) | null = null;

  constructor(
    private readonly config: SerialTransportConfig,
    private readonly logger: Logger,
    // Injected for testing - defaults to the real `SerialPort`. Tests pass
    // `SerialPortMock` (see test/transport/serial-transport.spec.ts).
    PortCtor: SerialPortCtor = SerialPort as unknown as SerialPortCtor,
  ) {
    this.port = new PortCtor({
      path: config.portPath,
      baudRate: config.baudRate,
      dataBits: ESSAE_CONFIRMED_SERIAL_PARAMETERS.dataBits,
      parity: "none",
      stopBits: ESSAE_CONFIRMED_SERIAL_PARAMETERS.stopBits,
      // flowControl: "none" - explicitly no hardware (RTS/CTS) and no
      // software (XON/XOFF) flow control, per the confirmed spec.
      rtscts: false,
      xon: false,
      xoff: false,
      xany: false,
      autoOpen: false,
    });

    // A real serial port can emit 'error' for reasons outside open()/
    // close() (e.g. the device is physically unplugged mid-session, or
    // the OS reports a driver-level failure). Without a listener, Node's
    // default EventEmitter behavior is to throw and crash the process on
    // an unhandled 'error' event - not acceptable for an unattended
    // gateway. This does not attempt to reconnect automatically; that
    // policy belongs to whatever calls this transport (Device/capture
    // layer), which does not exist yet for real hardware - see
    // docs/gateway-architecture.md §15.
    this.port.on("error", (err: Error) => {
      this.logger.error("Serial port error", { portPath: this.config.portPath, error: err.message });
    });
  }

  async open(): Promise<void> {
    this.logger.info("Opening serial port", {
      portPath: this.config.portPath,
      baudRate: this.config.baudRate,
      ...ESSAE_CONFIRMED_SERIAL_PARAMETERS,
    });
    await new Promise<void>((resolve, reject) => {
      this.port.open((err) => {
        if (err) {
          this.logger.error("Failed to open serial port", { portPath: this.config.portPath, error: err.message });
          reject(err);
          return;
        }
        this.logger.info("Serial port opened", { portPath: this.config.portPath });
        resolve();
      });
    });

    // Re-registering on every successful open() (including a reconnect
    // after close()) without first clearing any prior 'data' listener
    // would stack duplicate listeners across a disconnect/reconnect
    // cycle on the same instance, delivering each chunk to the handler
    // more than once. Guard against that explicitly rather than relying
    // on callers to only ever open() a transport once.
    this.port.removeAllListeners("data");
    this.port.on("data", (chunk: Buffer) => {
      this.dataHandler?.(chunk);
    });
  }

  async close(): Promise<void> {
    if (!this.port.isOpen) {
      return;
    }
    await new Promise<void>((resolve, reject) => {
      this.port.close((err) => {
        if (err) {
          this.logger.error("Failed to close serial port", { portPath: this.config.portPath, error: err.message });
          reject(err);
          return;
        }
        this.logger.info("Serial port closed", { portPath: this.config.portPath });
        resolve();
      });
    });
  }

  isOpen(): boolean {
    return this.port.isOpen;
  }

  onData(handler: (chunk: Buffer) => void): void {
    this.dataHandler = handler;
  }
}
