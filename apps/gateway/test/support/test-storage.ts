import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { SqliteLocalStorage } from "../../src/storage/sqlite-local-storage";
import type { GatewayConfig } from "../../src/config/config.types";
import { Logger } from "../../src/logging/logger";
import type { CreateLocalTransactionInput } from "../../src/storage/local-transaction.types";

/** Silences Logger output during tests without affecting assertions. */
export function silentLogger(component = "test"): Logger {
  const logger = new Logger(component);
  jest.spyOn(process.stdout, "write").mockImplementation(() => true);
  return logger;
}

export interface TestStorageHandle {
  tmpDir: string;
  dataDirectory: string;
  config: GatewayConfig;
  storage: SqliteLocalStorage;
  cleanup: () => void;
}

/** Creates a fresh, isolated SqliteLocalStorage backed by a temp directory. Caller must call init()/close() and cleanup(). */
export function createTestStorage(overrides: Partial<GatewayConfig> = {}): TestStorageHandle {
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-storage-test-"));
  const dataDirectory = path.join(tmpDir, "data");
  const config: GatewayConfig = {
    gatewayId: "gw-test-001",
    centreId: 42,
    cloudApiBaseUrl: "https://example.invalid",
    logDirectory: path.join(tmpDir, "logs"),
    dataDirectory,
    cloudAuthEmail: "gateway-test@ccmc.local",
    cloudAuthPassword: "test-only-password-never-real",
    ...overrides,
  };
  const storage = new SqliteLocalStorage(config, new Logger("test-storage"));
  return {
    tmpDir,
    dataDirectory,
    config,
    storage,
    cleanup: () => fs.rmSync(tmpDir, { recursive: true, force: true }),
  };
}

let sampleCounter = 0;

/**
 * A plausible, valid CreateLocalTransactionInput, with a fresh idempotency
 * key unless overridden. The key mixes in Date.now()/Math.random() (not
 * just the in-module counter) because `sampleCounter` resets to 0 in
 * every Jest test FILE (Jest gives each spec file its own module
 * registry, even under --runInBand) - a plain per-file counter alone
 * would produce the SAME "test-key-1" from two different spec files. That
 * collided for real once two cloud-integration spec files both called
 * this against the SAME live, not-reset-between-files Postgres database
 * (test/cloud-integration/no-data-loss.spec.ts and
 * critical-failure-scenario.spec.ts both got "test-key-1", so the second
 * one to run saw the first one's row as an idempotent duplicate instead
 * of creating its own). Purely in-memory SQLite tests never shared state
 * across files, so this was invisible until the cloud-integration suite
 * existed.
 */
export function sampleTransactionInput(overrides: Partial<CreateLocalTransactionInput> = {}): CreateLocalTransactionInput {
  sampleCounter += 1;
  const now = new Date().toISOString();
  return {
    localIdempotencyKey: `test-key-${Date.now()}-${Math.random().toString(36).slice(2, 8)}-${sampleCounter}`,
    centreId: 1,
    sourceId: 10,
    vehicleId: 20,
    quantityKg: 45.5,
    fat: 4.2,
    snf: 8.5,
    temperature: 4.0,
    capturedAt: now,
    ...overrides,
  };
}
