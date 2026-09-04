import * as path from "path";
import { spawn } from "child_process";
import { SqliteLocalStorage } from "../../src/storage/sqlite-local-storage";
import { createTestStorage } from "../support/test-storage";

/**
 * REAL process-kill tests, per the Checkpoint 3 instruction: "If
 * technically practical, also perform an actual subprocess/process-kill
 * test rather than only mocking process failure." These spawn a genuine
 * child Node process (via ts-node/register, no separate build step
 * needed) that opens the SAME SQLite database file, gets to a precise,
 * signaled point in a real transaction, and is then killed with SIGKILL -
 * an actual OS-level process death, not a simulated one. The parent then
 * reopens the database (a real SqliteLocalStorage instance, exactly as
 * the gateway would on restart) and asserts on what actually persisted.
 *
 * Timing is synchronized via a stdout line from the child (not a fixed
 * sleep+race) so these tests are not flaky: the parent waits for the
 * child's exact "I have reached the point I want killed at" signal before
 * sending SIGKILL.
 */

const KILL_BEFORE_COMMIT_SCRIPT = path.join(__dirname, "support", "kill-before-commit.ts");
const KILL_DURING_PROCESSING_SCRIPT = path.join(__dirname, "support", "kill-during-processing.ts");

/** Spawns `script` as a real child process, waits for `readyLine` on stdout, sends SIGKILL, then waits for exit. */
function runAndKillAfterLine(
  script: string,
  args: string[],
  readyLine: string,
): Promise<{ killedInTime: boolean; exitSignal: string | null }> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["-r", "ts-node/register/transpile-only", script, ...args], {
      stdio: ["ignore", "pipe", "pipe"],
    });

    let stdoutBuffer = "";
    let stderrBuffer = "";
    let killedInTime = false;
    const timeout = setTimeout(() => {
      reject(new Error(`Timed out waiting for "${readyLine}" from child. stdout so far: ${stdoutBuffer}\nstderr: ${stderrBuffer}`));
    }, 8000);

    child.stdout.on("data", (chunk: Buffer) => {
      stdoutBuffer += chunk.toString();
      if (!killedInTime && stdoutBuffer.includes(readyLine)) {
        killedInTime = true;
        clearTimeout(timeout);
        child.kill("SIGKILL");
      }
    });
    child.stderr.on("data", (chunk: Buffer) => {
      stderrBuffer += chunk.toString();
    });

    child.on("exit", (_code, signal) => {
      clearTimeout(timeout);
      resolve({ killedInTime, exitSignal: signal });
    });
    child.on("error", (err) => {
      clearTimeout(timeout);
      reject(err);
    });
  });
}

describe("Restart recovery - real subprocess kill", () => {
  jest.setTimeout(20_000);

  // TEST 4, real-process version: a process killed AFTER both INSERTs ran
  // but BEFORE COMMIT must leave no partial state once the DB is reopened.
  it("a process SIGKILLed before COMMIT leaves no partial local_transaction/outbox state", async () => {
    const handle = createTestStorage();
    // Bootstrap: run migrations + gateway_metadata via a real init(), then
    // close, so the child process opens an already-valid schema (matching
    // what a real gateway restart scenario looks like - the schema is
    // never created by the thing that crashes).
    await handle.storage.init();
    await handle.storage.close();

    const key = "subprocess-kill-before-commit";
    const dbPath = path.join(handle.dataDirectory, "gateway.sqlite");

    const { killedInTime, exitSignal } = await runAndKillAfterLine(KILL_BEFORE_COMMIT_SCRIPT, [dbPath, key], "READY_TO_COMMIT");
    expect(killedInTime).toBe(true);
    expect(exitSignal).toBe("SIGKILL");

    // Reopen exactly as the real gateway would on restart.
    const reopened = new SqliteLocalStorage(handle.config);
    await reopened.init();

    const tx = await reopened.getLocalTransactionByIdempotencyKey(key);
    expect(tx).toBeNull(); // the uncommitted INSERT never happened, as far as SQLite is concerned

    const allTx = await reopened.listLocalTransactions();
    expect(allTx.filter((t) => t.localIdempotencyKey === key)).toHaveLength(0);
    const allOutbox = await reopened.listOutboxRecords();
    expect(allOutbox.filter((o) => o.localIdempotencyKey === key)).toHaveLength(0);

    await reopened.close();
    handle.cleanup();
  });

  // TEST 2, real-process version: a process killed AFTER commit + claim
  // (PROCESSING) must have that item recovered to PENDING on restart.
  it("a process SIGKILLed while an outbox item is PROCESSING is recovered to PENDING on restart", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    await handle.storage.close();

    const key = "subprocess-kill-during-processing";
    const dbPath = path.join(handle.dataDirectory, "gateway.sqlite");

    const { killedInTime, exitSignal } = await runAndKillAfterLine(KILL_DURING_PROCESSING_SCRIPT, [dbPath, key], "CLAIMED");
    expect(killedInTime).toBe(true);
    expect(exitSignal).toBe("SIGKILL");

    const reopened = new SqliteLocalStorage(handle.config); // init() runs the startup recovery sweep
    await reopened.init();

    const tx = await reopened.getLocalTransactionByIdempotencyKey(key);
    expect(tx).not.toBeNull(); // the committed transaction DOES survive - only the outbox claim was orphaned

    const outbox = await reopened.getOutboxRecordByLocalTransactionId(tx!.id);
    expect(outbox?.status).toBe("PENDING"); // recovered, not stuck PROCESSING forever
    expect(outbox?.claimedAt).toBeNull();

    const eligible = await reopened.findEligibleOutboxItems(new Date().toISOString(), 10);
    expect(eligible.map((o) => o.id)).toContain(outbox!.id);

    await reopened.close();
    handle.cleanup();
  });
});
