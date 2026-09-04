import * as fs from "fs";
import * as path from "path";
import { Gateway } from "./gateway";
import { loadConfig, resolveConfigPath, ConfigValidationError } from "./config/config.loader";
import { Logger } from "./logging/logger";
import { LocalApiServer } from "./local-api/local-api-server";

/**
 * Reads this package's own version from package.json rather than
 * hardcoding a string that would inevitably drift from the real one.
 * Works both under ts-node (src/main.ts -> ../package.json) and compiled
 * output (dist/main.js -> ../package.json), since both sit exactly one
 * directory below the package root.
 */
function readOwnVersion(): string {
  const pkgPath = path.join(__dirname, "..", "package.json");
  const pkg = JSON.parse(fs.readFileSync(pkgPath, "utf-8")) as { version: string };
  return pkg.version;
}

/**
 * Entry point. Two modes:
 *
 *  - Default (no args): long-running gateway process. Loads config, starts
 *    the Gateway (and, if config.localApi.enabled, the local read-only
 *    HTTP API - see src/local-api/local-api-server.ts), and stays alive
 *    until SIGINT/SIGTERM, at which point it shuts down cleanly and exits
 *    0. This is what WinSW's <executable>/<arguments> will actually
 *    launch on a real deployment.
 *  - `--status` / `--version`: one-shot diagnostic mode. Starts the
 *    Gateway just long enough to produce a health snapshot, prints it as
 *    JSON to stdout, stops the Gateway, and exits immediately. This is the
 *    "diagnostic command" the original directive asked for in lieu of any
 *    UI (Rule 6: no gateway frontend).
 *
 * The local API is deliberately wired ONLY into the default long-running
 * branch, never into diagnostic mode or scripts/simulate-capture-cli.ts
 * (both of which also construct/start a Gateway against the same
 * gateway.sqlite): all three could otherwise race to bind the same TCP
 * port. The long-running gateway process is the one and only owner of
 * that port for a given install.
 */
async function main(): Promise<void> {
  const args = process.argv.slice(2);
  const diagnosticMode = args.includes("--status") || args.includes("--version");

  const version = readOwnVersion();
  const configPath = resolveConfigPath();

  let config;
  try {
    config = loadConfig(configPath);
  } catch (err) {
    if (err instanceof ConfigValidationError) {
      process.stderr.write(`Gateway configuration error: ${err.message}\n`);
      process.exitCode = 1;
      return;
    }
    throw err;
  }

  const logger = new Logger("gateway");
  const gateway = new Gateway(config, version, logger);

  if (diagnosticMode) {
    // recoverStaleProcessing: false - this diagnostic call may run
    // concurrently with an already-running long-lived gateway service
    // instance against the same gateway.sqlite file (e.g. an operator
    // running `--status` against a live Windows service). The unconditional
    // startup sweep's "fresh process, no claims yet, every PROCESSING row
    // is orphaned" assumption is false in that case - it could incorrectly
    // requeue a genuinely in-flight PROCESSING row out from under the
    // running service. Skipping it here does not lose the pendingSyncCount
    // signal (countPendingOutbox() already counts PROCESSING rows). See
    // storage.types.ts's init() doc comment for the full reasoning.
    await gateway.start({ recoverStaleProcessing: false });
    const snapshot = await gateway.getHealthSnapshot();
    process.stdout.write(JSON.stringify(snapshot, null, 2) + "\n");
    await gateway.stop();
    return;
  }

  await gateway.start();

  let localApiServer: LocalApiServer | null = null;
  if (config.localApi?.enabled) {
    localApiServer = new LocalApiServer(config, gateway.storage, gateway.health, logger.child("local-api"));
    try {
      await localApiServer.start();
    } catch (err) {
      // A failure to bind the local API port (e.g. already in use) is
      // real, but it must not take down the gateway's actual job (device
      // capture + cloud sync) - the local dashboard is an operator
      // convenience layered on top of that, not a dependency of it. Log
      // loudly and continue running without the local API rather than
      // exiting.
      logger.error("Failed to start local API - continuing without it", { error: (err as Error).message });
      localApiServer = null;
    }
  }

  // Keep the event loop alive. There is no server/listener yet to do this
  // implicitly (Checkpoint 2 has no HTTP server - Rule 6), so an explicit
  // interval is the simplest boring mechanism; it does no work itself and
  // is cleared on shutdown below for a clean exit. (The local API's own
  // http.Server, when started, would also keep the process alive on its
  // own - this interval is kept regardless so shutdown behavior doesn't
  // depend on whether config.localApi.enabled happens to be set.)
  const keepAlive = setInterval(() => {}, 1 << 30);

  let shuttingDown = false;
  const shutdown = (signal: string) => {
    if (shuttingDown) return;
    shuttingDown = true;
    clearInterval(keepAlive);
    logger.info("Received shutdown signal", { signal });
    (localApiServer ? localApiServer.stop() : Promise.resolve())
      .then(() => gateway.stop())
      .then(() => {
        process.exit(0);
      })
      .catch((err) => {
        logger.error("Error during shutdown", { error: (err as Error).message });
        process.exit(1);
      });
  };

  process.on("SIGINT", () => shutdown("SIGINT"));
  process.on("SIGTERM", () => shutdown("SIGTERM"));
}

main().catch((err) => {
  process.stderr.write(`Fatal error: ${(err as Error).stack ?? (err as Error).message}\n`);
  process.exit(1);
});
