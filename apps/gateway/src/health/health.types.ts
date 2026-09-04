/** Lifecycle state of the gateway process itself. */
export type GatewayServiceState = "STARTING" | "RUNNING" | "STOPPING" | "STOPPED";

/**
 * Connectivity state for something the gateway talks to (cloud API, a
 * device). UNKNOWN is the honest default before any real check has been
 * made - it is deliberately distinct from CONNECTED/DISCONNECTED so we
 * never claim a state we haven't actually observed.
 */
export type ConnectivityState = "UNKNOWN" | "CONNECTED" | "DISCONNECTED";

/**
 * The gateway's full health/status snapshot, per the original directive's
 * required fields: Gateway ID, Centre ID, Version, Uptime, Cloud
 * connectivity, Device connectivity, Pending sync count.
 *
 * For Checkpoint 2, this is exposed only via the --status CLI flag and via
 * structured log lines (no HTTP endpoint, no UI - Rule 6: no gateway
 * frontend). Fields not yet wired to real subsystems are explicitly
 * commented with which future checkpoint wires them, so nothing here reads
 * as "implemented" before it is.
 */
export interface GatewayHealthSnapshot {
  gatewayId: string;
  centreId: number;
  version: string;
  /** ISO-8601 timestamp of when start() completed, or null if not started yet. */
  startedAt: string | null;
  uptimeSeconds: number;
  serviceState: GatewayServiceState;

  /**
   * Always "UNKNOWN" in Checkpoint 2 - no cloud API calls are made yet.
   * Wired to a real value in Checkpoint 5 (cloud integration) once the
   * gateway actually attempts HTTPS calls and can observe success/failure.
   */
  cloudConnectivity: ConnectivityState;

  /**
   * Always "UNKNOWN" in Checkpoint 2 - DeviceManager has zero registered
   * devices this checkpoint (no simulators yet). Wired to a real aggregate
   * value in Checkpoint 4 once simulator-backed devices exist to report
   * connect/disconnect state.
   */
  deviceConnectivity: ConnectivityState;

  /**
   * Always 0 in Checkpoint 2 - there is no local storage or outbox yet.
   * Wired to the real SQLite outbox's pending-row count in Checkpoint 3.
   */
  pendingSyncCount: number;
}
