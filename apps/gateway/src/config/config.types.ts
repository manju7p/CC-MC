/**
 * Gateway configuration shape.
 *
 * Grew from Checkpoint 2's minimal bootstrap/health-reporting fields
 * through Checkpoint 3's dataDirectory to Checkpoint 5's cloud
 * credentials below.
 *
 * On credentials: docs/gateway-decision.md §9 always said the gateway's
 * cloud auth credentials belong "in config\gateway.config.json (or a
 * Windows-native secret store)" - Checkpoint 5 is where that plan
 * actually gets built. This file is exactly as sensitive as
 * apps/api/.env is for the cloud API (same pattern: plain JSON/dotenv,
 * gitignored - see config/gateway.config.example.json, which is
 * committed with a placeholder, never real credentials). Real-Windows
 * folder-ACL protection for this file is a Phase 12 concern per
 * gateway-decision.md §9/§10, not solved by this checkpoint - see
 * docs/gateway-architecture.md's cloud-sync section for the full
 * discussion, including why a config file (not an OS secret store) is
 * the right amount of engineering for this MVP.
 */
export interface GatewayConfig {
  /** Stable identifier for this gateway installation, e.g. "gw-blr-001". */
  gatewayId: string;

  /** The chilling centre (cloud-side Centre.id) this gateway serves. */
  centreId: number;

  /** Base URL of the cloud API this gateway syncs to, e.g. "https://ccmc-api.example.org". */
  cloudApiBaseUrl: string;

  /**
   * Directory the gateway's own structured logs are written to.
   * (Distinct from WinSW's own wrapper-level logs - see
   * docs/gateway-decision.md §7.)
   */
  logDirectory: string;

  /**
   * Directory the gateway's local SQLite database (gateway.sqlite) lives
   * in - added Checkpoint 3. Matches the deployment layout's `data\`
   * folder (docs/gateway-decision.md §1), kept separate from `app\` and
   * `logs\` so an application upgrade never touches it. See
   * docs/gateway-architecture.md for the schema this database holds.
   */
  dataDirectory: string;

  /**
   * Email of the gateway's cloud service account (Checkpoint 5) - a
   * regular User row in the cloud database, scoped to exactly this
   * gateway's one centre, holding only the RECEPTION_CREATE permission.
   * See apps/api/src/seed.ts's GatewayService role and
   * docs/gateway-architecture.md's cloud-sync section for the full
   * authentication design. Authenticated the exact same way any human
   * user is: POST /auth/login.
   */
  cloudAuthEmail: string;

  /**
   * Password for cloudAuthEmail. NEVER logged (see src/sync/cloud-auth.ts
   * and http-cloud-client.ts's explicit "never log this" doc comments) -
   * every log statement anywhere in the sync path logs IDs
   * (localIdempotencyKey, outbox id, attempt number, HTTP status), never
   * this value, the resulting JWT, or the Authorization header.
   */
  cloudAuthPassword: string;

  /**
   * Optional RS232 serial transport configuration for real hardware
   * (added in response to CEO-confirmed ESSAE serial transport
   * parameters - see src/transport/serial-transport.ts and
   * docs/gateway-architecture.md §15). Optional because a gateway may
   * still be running purely against simulator devices (no real hardware
   * wired up yet) - see docs/gateway-architecture.md §9.
   *
   * When present, BOTH fields are required. `baudRate` is the one
   * CEO-confirmed-configurable parameter (9600 for the current ESSAE
   * equipment, but must remain settable per install/equipment change,
   * not hardcoded). `portPath` is the OS-specific serial device
   * identifier (e.g. "COM3" on Windows) - not something the CEO was
   * asked about, but structurally required and necessarily per-install.
   *
   * The other three RS232 parameters (data bits, parity, stop bits) and
   * flow control are FIXED, CEO-confirmed constants
   * (ESSAE_CONFIRMED_SERIAL_PARAMETERS in
   * src/transport/serial-transport.types.ts) - deliberately NOT part of
   * this config, because they are confirmed facts about this equipment,
   * not per-install tuning knobs an operator should be exposed to.
   *
   * This does NOT configure the ESSAE message protocol (packet format,
   * commands, checksums) - only the byte-level transport. See
   * src/parser/parser.types.ts and docs/gateway-architecture.md §15 for
   * why that remains unimplemented.
   */
  serial?: {
    /** OS-specific serial port path/identifier, e.g. "COM3" or "/dev/ttyUSB0". */
    portPath: string;
    /** Baud rate - 9600 for the current ESSAE equipment, per the CEO. Configurable, not hardcoded. */
    baudRate: number;
  };

  /**
   * Optional local (edge) read-only HTTP API, added for offline operator
   * visibility - see docs/gateway-architecture.md's "Local vs. cloud
   * responsibility" section. Optional and off by default: a gateway with
   * no operator-facing local dashboard configured should not open a port
   * it doesn't need.
   *
   * This is a DIFFERENT trust domain than the cloud's JWT_SECRET
   * deliberately - the local API never verifies a cloud JWT and the
   * cloud never sees this token. Distributing JWT_SECRET to every centre
   * machine so it could verify cloud tokens locally would be a real
   * regression to the cloud's own signing-secret blast radius; a
   * separate, per-gateway shared token is a smaller, more honestly-scoped
   * trade-off for a read-only, single-centre, LAN/localhost-only surface.
   *
   * Only ever exposes read-only operational data already durable in
   * gateway.sqlite (today's local transactions, outbox/sync status,
   * gateway health) - never SQLite access itself, never cloud
   * credentials, never write endpoints. See src/local-api/local-api-server.ts.
   */
  localApi?: {
    /** Whether to start the local API at all. Off by default. */
    enabled: boolean;
    /** TCP port the local API listens on. */
    port: number;
    /**
     * Interface to bind. Defaults to "127.0.0.1" (localhost-only) if
     * omitted - deliberately conservative per the "bind only to localhost
     * unless LAN access is explicitly required" instruction. Set to
     * "0.0.0.0" (or a specific LAN interface address) only if operators on
     * other machines on the centre's own network genuinely need this, and
     * only after opening the corresponding Windows Firewall rule.
     */
    bindAddress?: string;
    /**
     * Shared secret the local frontend page must send as
     * `Authorization: Bearer <accessToken>`. Stored the same way
     * cloudAuthPassword already is (this same gitignored file) - not a
     * JWT, not verified against the cloud, and not usable against the
     * cloud API in any way.
     */
    accessToken: string;
  };
}
