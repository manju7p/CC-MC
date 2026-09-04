import * as net from "net";
import * as path from "path";
import { execFileSync, spawn, type ChildProcess } from "child_process";

/**
 * Spins up a REAL, complete cloud stack for Checkpoint 5's integration
 * tests: a dedicated PostgreSQL database, reset and seeded exactly the
 * way apps/api's own e2e suite is (test/global-setup.ts's pattern -
 * synchronize(true) then seed()), and the REAL compiled NestJS API
 * listening on a real port. Nothing here is mocked - test files in this
 * directory make real HTTP requests, through the real gateway
 * HttpCloudClient, against a real running server backed by a real
 * database.
 *
 * A SEPARATE database (ccmc_gateway_test, not apps/api's own ccmc_test)
 * so this suite can run independently of (and concurrently with) apps/api's
 * own `pnpm test:e2e` without either resetting data out from under the
 * other.
 *
 * Deliberately adds NO new dependency to apps/gateway's package.json:
 * the database reset/seed step runs as a child `node` process with its
 * cwd set to apps/api, so Node's own module resolution finds typeorm/pg
 * from apps/api's already-installed dependencies - gateway's own
 * package.json stays exactly as boring as Rule 11 asks (this is test
 * tooling, not gateway runtime code, but the same "don't add a
 * dependency you don't need" instinct applies).
 */

const API_DIR = path.resolve(__dirname, "..", "..", "..", "..", "api");

export const CLOUD_TEST_DATABASE_NAME = "ccmc_gateway_test";
export const CLOUD_TEST_DATABASE_URL = `postgresql://postgres:postgres@localhost:5432/${CLOUD_TEST_DATABASE_NAME}`;
export const CLOUD_TEST_JWT_SECRET = "gateway-cloud-integration-test-secret-never-used-elsewhere";
/**
 * Deliberately short (not the real 8h default) - this suite's
 * auth-expiry test needs a token that actually expires within a test's
 * lifetime. Every OTHER test in this suite logs in fresh via its own
 * HttpCloudClient and completes in well under this window, so a short
 * expiry costs nothing for them and buys the auth-expiry test a real,
 * fast, non-mocked expiry proof instead of a mocked clock.
 */
export const CLOUD_TEST_JWT_EXPIRES_IN = "3s";
export const CLOUD_TEST_PORT = 3901;
export const CLOUD_TEST_BASE_URL = `http://127.0.0.1:${CLOUD_TEST_PORT}`;

// Seeded by apps/api/src/seed.ts - see that file's Checkpoint 5 additions.
export const GATEWAY_BLR_EMAIL = "gateway-blr-cc-01@ccmc.local";
export const GATEWAY_BLR_PASSWORD = "GatewayBLR@2026!sync";
export const GATEWAY_MYS_EMAIL = "gateway-mys-cc-01@ccmc.local";
export const GATEWAY_MYS_PASSWORD = "GatewayMYS@2026!sync";
export const BLR_CENTRE_ID = 1;
export const MYS_CENTRE_ID = 2;

/** Idempotent: creates ccmc_gateway_test if it doesn't already exist. */
function ensureDatabaseExists(): void {
  try {
    execFileSync("createdb", ["-h", "localhost", "-U", "postgres", CLOUD_TEST_DATABASE_NAME], {
      env: { ...process.env, PGPASSWORD: "postgres" },
      stdio: "pipe",
    });
  } catch (err) {
    const stderr = String((err as { stderr?: Buffer | string }).stderr ?? (err as Error).message ?? "");
    if (!stderr.includes("already exists")) {
      throw new Error(`Failed to create ${CLOUD_TEST_DATABASE_NAME}: ${stderr}`);
    }
  }
}

/**
 * Drops and recreates the schema, then seeds it - identical in spirit to
 * apps/api/test/global-setup.ts, just targeting ccmc_gateway_test instead
 * of ccmc_test, and run as a child process (see file header) rather than
 * an in-process TypeORM DataSource.
 */
export function resetAndSeedDatabase(): void {
  ensureDatabaseExists();

  const script = [
    'const { DataSource } = require("typeorm");',
    'const { ALL_ENTITIES } = require("./dist/entities");',
    'const { seed } = require("./dist/seed");',
    "(async () => {",
    '  const ds = new DataSource({ type: "postgres", url: process.env.DATABASE_URL, entities: ALL_ENTITIES, synchronize: false });',
    "  await ds.initialize();",
    "  await ds.synchronize(true);",
    "  await seed(ds);",
    "  await ds.destroy();",
    "})().catch((e) => { console.error(e); process.exit(1); });",
  ].join("\n");

  execFileSync("node", ["-e", script], {
    cwd: API_DIR,
    env: { ...process.env, DATABASE_URL: CLOUD_TEST_DATABASE_URL },
    stdio: "inherit",
  });
}

let apiProcess: ChildProcess | null = null;

/** Spawns the REAL compiled `apps/api/dist/main.js`, bound to CLOUD_TEST_PORT and CLOUD_TEST_DATABASE_URL. Idempotent. */
export async function startLiveApi(): Promise<void> {
  if (apiProcess) return;

  let stderrBuffer = "";
  const child = spawn("node", ["dist/main.js"], {
    cwd: API_DIR,
    env: {
      ...process.env,
      DATABASE_URL: CLOUD_TEST_DATABASE_URL,
      JWT_SECRET: CLOUD_TEST_JWT_SECRET,
      JWT_EXPIRES_IN: CLOUD_TEST_JWT_EXPIRES_IN,
      PORT: String(CLOUD_TEST_PORT),
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stderr?.on("data", (chunk: Buffer) => {
    stderrBuffer += chunk.toString();
  });
  child.on("exit", (code) => {
    if (code !== null && code !== 0) {
      // eslint-disable-next-line no-console
      console.error(`[cloud-integration] live API process exited early (code ${code}):\n${stderrBuffer}`);
    }
  });
  apiProcess = child;

  await waitForPortOpen(CLOUD_TEST_PORT, 20_000);
}

/** Kills the spawned API process, if one is running. Safe to call more than once. */
export function stopLiveApi(): void {
  if (apiProcess) {
    apiProcess.kill("SIGTERM");
    apiProcess = null;
  }
}

/**
 * Runs a single scalar SQL query directly against ccmc_gateway_test via
 * `psql` (no `pg` driver dependency needed in gateway - see file header)
 * and returns the trimmed text result. Used only to make ground-truth
 * assertions ("exactly one row", "exactly one audit event") that go
 * beneath what the HTTP API itself would tell us - the same reason
 * TEST 3b in storage/local-transactions.spec.ts bypasses application code
 * to check a raw constraint. Every caller is expected to interpolate only
 * values this test suite generated itself (UUIDs, numeric ids) - not
 * arbitrary/external input - so this stays a test-only convenience, never
 * a pattern to copy into gateway runtime code.
 */
export function queryScalar(sql: string): string {
  // Options MUST precede the positional connection-string argument here.
  // psql accepts at most two trailing positionals (dbname [username]); if
  // the connection URL is given first, "-t" gets silently absorbed as a
  // second positional ("username") and "-A"/"-c"/<sql> are then rejected
  // as three illegal extra arguments. That happens to be tolerated by at
  // least some Linux psql builds but is fatal on Windows' psql.exe, where
  // it prints three "extra command-line argument ... ignored" warnings,
  // psql runs with no query at all, and this function returns "" - which
  // parseInt() upstream turns into NaN for every caller. Keeping all
  // options before the single positional dbname/URL avoids this entirely
  // and matches the already-safe pattern used by ensureDatabaseExists()'s
  // `createdb` call above.
  const output = execFileSync("psql", ["-t", "-A", "-c", sql, CLOUD_TEST_DATABASE_URL], {
    env: { ...process.env, PGPASSWORD: "postgres" },
    encoding: "utf-8",
  });
  return output.trim();
}

function sqlLiteral(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

/** Counts milk_reception_transactions rows for a given localIdempotencyKey - should be exactly 1 once synced, 0 before. */
export function countReceptionRowsForKey(localIdempotencyKey: string): number {
  return parseInt(
    queryScalar(`SELECT count(*) FROM milk_reception_transactions WHERE "localIdempotencyKey" = ${sqlLiteral(localIdempotencyKey)}`),
    10,
  );
}

/** Counts audit_logs rows recording a RECEPTION_CREATE for the given cloud transaction id. */
export function countReceptionCreateAuditEvents(cloudTransactionId: number): number {
  return parseInt(
    queryScalar(
      `SELECT count(*) FROM audit_logs WHERE action = 'RECEPTION_CREATE' AND "resourceId" = ${sqlLiteral(String(cloudTransactionId))}`,
    ),
    10,
  );
}

function waitForPortOpen(port: number, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve, reject) => {
    const attempt = () => {
      const socket = net.createConnection({ port, host: "127.0.0.1" });
      socket.once("connect", () => {
        socket.end();
        resolve();
      });
      socket.once("error", () => {
        socket.destroy();
        if (Date.now() > deadline) {
          reject(new Error(`Live API did not start listening on port ${port} within ${timeoutMs}ms`));
        } else {
          setTimeout(attempt, 150);
        }
      });
    };
    attempt();
  });
}
