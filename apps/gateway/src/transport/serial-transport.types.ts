/**
 * Configuration for the generic RS232 serial transport (`SerialTransport`).
 *
 * ONLY `portPath` and `baudRate` are here, and both are REQUIRED, real
 * per-install values - neither is guessed:
 *   - `baudRate` is configurable per the CEO's explicit instruction ("must
 *     be configurable through the application/settings page") - see
 *     `GatewayConfig.serial` in `../config/config.types.ts` and
 *     `docs/gateway-architecture.md` §15 for where that value actually
 *     lives today (the local `gateway.config.json` file - there is no
 *     web-facing settings page in this repository yet, see that section
 *     for why one was not invented here).
 *   - `portPath` is the OS-specific serial device identifier (e.g. `COM3`
 *     on Windows, `/dev/ttyUSB0` on Linux). This is not something the CEO
 *     was asked about, but it is structurally required to open ANY serial
 *     port - every physical install's COM port assignment differs, so
 *     this cannot be a fixed constant either. Treated the same way as
 *     `baudRate`: a required, per-install configuration value.
 *
 * See `ESSAE_CONFIRMED_SERIAL_PARAMETERS` below for the four remaining
 * RS232 parameters, which are FIXED, not configurable.
 */
export interface SerialTransportConfig {
  /** OS-specific serial port path/identifier, e.g. "COM3" or "/dev/ttyUSB0". */
  portPath: string;
  /** Baud rate. Configurable - the CEO confirmed 9600 for the current ESSAE equipment, but this must remain settable, not hardcoded. */
  baudRate: number;
}

/**
 * The four RS232 parameters other than baud rate, as explicitly confirmed
 * by the CEO for the ESSAE equipment (see the "NEW INFORMATION FROM CEO"
 * directive this constant was added in response to, and
 * `docs/gateway-architecture.md` §15). These are FIXED CONSTANTS, not user
 * -configurable settings: they are now known, confirmed facts about this
 * specific equipment, not tuning knobs. Do not expose these as editable
 * settings, and do not change these values without an explicit new
 * confirmation from the equipment vendor/CEO - the same rule that forbids
 * inventing them in the first place also forbids treating them as freely
 * adjustable once known.
 *
 * IMPORTANT: these are TRANSPORT parameters only (how bytes move over the
 * wire). They say nothing about the ESSAE MESSAGE PROTOCOL running on top
 * of this transport (packet format, framing, checksums, commands, field
 * positions) - that specification has not been supplied and is NOT
 * implemented anywhere in this repository. See `../parser/parser.types.ts`
 * and `docs/gateway-architecture.md` §15 for that boundary.
 */
export const ESSAE_CONFIRMED_SERIAL_PARAMETERS = {
  dataBits: 8,
  parity: "none",
  stopBits: 1,
  /** No flow control: neither hardware (RTS/CTS) nor software (XON/XOFF). */
  flowControl: "none",
} as const;
