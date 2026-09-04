/**
 * Placeholder for the offline-first sync engine (local transaction ->
 * SQLite outbox -> attempt upload -> synced/pending+retry+backoff).
 *
 * Deliberately NOT implemented in Checkpoint 2, per the explicit
 * instruction to not touch sync/idempotency/cloud-integration until
 * Checkpoints 3/5/6. Only the lifecycle shape (start/stop) exists here so
 * Gateway.start()/stop() has a concrete seam to call into once a real
 * SyncEngine implementation exists - Gateway itself won't need to change
 * when that happens.
 */
export interface SyncEngine {
  start(): Promise<void>;
  stop(): Promise<void>;
}
