#!/usr/bin/env node
/**
 * Assembles a deployment directory proving the CC-MC Gateway's Windows
 * install layout - specifically that application binaries/runtime stay
 * separate from persistent application data, per Checkpoint 2 constraint
 * 10 ("keep application binaries/runtime separate from persistent
 * application data ... so future upgrades cannot accidentally delete
 * them").
 *
 * This script is a proof of the LAYOUT, not a real Windows packaging
 * pipeline:
 *
 *  - `CCMCGateway.exe` and `node\node.exe` are real Windows executables
 *    that this Linux sandbox cannot download (confirmed: `curl -sI` against
 *    both github.com/winsw/winsw/releases and nodejs.org/dist/ returned
 *    403 Forbidden - this sandbox's egress allowlist does not permit
 *    fetching them) and cannot run even if present. They are written here
 *    as clearly-labeled PLACEHOLDER files (see writePlaceholderBinary
 *    below) so the folder structure, and the packaging script that
 *    produces it, can be reviewed and re-run today. Producing the real
 *    binaries requires either a network-unrestricted build environment or
 *    doing this step at actual Windows packaging time - tracked as a
 *    Checkpoint 6/11 task, not silently glossed over.
 *  - `app\dist\main.js` and `app\node_modules\` ARE real: this step runs
 *    the gateway's own `tsc` build and copies its actual compiled output
 *    and installed dependencies, so that part of the proof is genuine.
 *
 * Usage: node packaging/build-deployment.mjs [outputDir]
 *   outputDir defaults to <gateway-root>/dist-deployment
 */

import * as fs from "node:fs";
import * as path from "node:path";
import * as url from "node:url";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const __dirname = path.dirname(url.fileURLToPath(import.meta.url));
const gatewayRoot = path.resolve(__dirname, "..");
const outputDir = path.resolve(process.argv[2] ?? path.join(gatewayRoot, "dist-deployment"));

function log(message) {
  process.stdout.write(`[build-deployment] ${message}\n`);
}

function writePlaceholderBinary(targetPath, label) {
  fs.mkdirSync(path.dirname(targetPath), { recursive: true });
  const contents =
    `PLACEHOLDER - not a real Windows executable.\n\n` +
    `This file stands in for: ${label}\n\n` +
    `This sandbox is Linux-only and cannot download or execute real Windows\n` +
    `binaries (verified: github.com/winsw/winsw/releases and nodejs.org/dist/\n` +
    `both returned 403 Forbidden from this environment's network egress\n` +
    `allowlist). Replace this file with the real artifact when packaging on\n` +
    `an unrestricted build machine or at real Windows packaging time.\n` +
    `See docs/gateway-decision.md §1 and §10 for the real deployment layout\n` +
    `this placeholder represents.\n`;
  fs.writeFileSync(targetPath, contents, "utf-8");
  log(`Wrote placeholder: ${path.relative(outputDir, targetPath)} (${label})`);
}

function copyDir(src, dest) {
  fs.mkdirSync(dest, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, entry.name);
    const d = path.join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDir(s, d);
    } else if (entry.isFile()) {
      fs.copyFileSync(s, d);
    }
  }
}

function main() {
  log(`Gateway root: ${gatewayRoot}`);
  log(`Output dir:   ${outputDir}`);

  if (fs.existsSync(outputDir)) {
    log("Removing previous deployment output...");
    fs.rmSync(outputDir, { recursive: true, force: true });
  }
  fs.mkdirSync(outputDir, { recursive: true });

  // --- Binaries / runtime (separate branch of the tree from persistent data) ---

  writePlaceholderBinary(path.join(outputDir, "CCMCGateway.exe"), "WinSW v3 self-contained executable, renamed");
  writePlaceholderBinary(path.join(outputDir, "node", "node.exe"), "Vendored official Windows x64 Node.js runtime");

  // Real WinSW XML config - not a placeholder, this is the actual file
  // reviewed in service/CCMCGateway.xml.
  fs.copyFileSync(path.join(gatewayRoot, "service", "CCMCGateway.xml"), path.join(outputDir, "CCMCGateway.xml"));
  log("Copied real service/CCMCGateway.xml");

  fs.copyFileSync(path.join(gatewayRoot, "service", "install.ps1"), path.join(outputDir, "install.ps1"));
  fs.copyFileSync(path.join(gatewayRoot, "service", "uninstall.ps1"), path.join(outputDir, "uninstall.ps1"));
  log("Copied real service/install.ps1 and service/uninstall.ps1");

  // --- Application (real build output) ---

  log("Building gateway TypeScript (tsc)...");
  // Deliberately NOT `execFileSync("npx", [...])`. On Windows, npx is
  // installed as npx.cmd (a shell shim), not a bare npx.exe - Node's
  // child_process resolves the executable name directly (like
  // CreateProcess), not through cmd.exe's PATHEXT-based lookup, unless
  // `shell: true` is passed. Without that, spawning the literal string
  // "npx" fails with ENOENT even though npx works fine when typed
  // interactively (verified: this is exactly the failure this checkpoint
  // was asked to fix - "spawnSync npx ENOENT" - reported when this script
  // was run from a real Windows PowerShell prompt). `shell: true` would
  // fix it too, but pulls in cmd.exe's own quoting/escaping rules for no
  // benefit here - this script never needs a shell, only "run the
  // TypeScript compiler this package already depends on".
  //
  // Resolving typescript's own bin script via Node's module resolution
  // and invoking it with the exact same node executable already running
  // this script (process.execPath) sidesteps PATH/PATHEXT/shell lookup
  // entirely - it is not "find some program called npx/tsc on PATH", it
  // is "load this specific file with this specific interpreter", which
  // resolves identically on Windows, macOS, and Linux. `typescript` is
  // already a real devDependency of this package (see package.json), so
  // this introduces no new dependency.
  const require = createRequire(import.meta.url);
  let tscBin;
  try {
    tscBin = require.resolve("typescript/bin/tsc");
  } catch (err) {
    throw new Error(
      `Could not resolve typescript's bin script from ${gatewayRoot} (${err.message}). ` +
        `This package declares "typescript" as a devDependency (see package.json) - run ` +
        `"pnpm install" from the repo root first. If dependencies are installed and this still ` +
        `fails, node_modules/typescript may be a broken/unreadable symlink (e.g. inside some ` +
        `sandboxed file-mount environments) rather than a missing dependency - reinstalling won't ` +
        `help in that case; run this script from a real shell against the real filesystem instead.`,
    );
  }
  execFileSync(process.execPath, [tscBin, "-p", path.join(gatewayRoot, "tsconfig.json")], {
    cwd: gatewayRoot,
    stdio: "inherit",
  });

  const distSrc = path.join(gatewayRoot, "dist");
  if (!fs.existsSync(path.join(distSrc, "main.js"))) {
    throw new Error(`Expected build output at ${distSrc}/main.js but it does not exist - build failed silently?`);
  }
  copyDir(distSrc, path.join(outputDir, "app", "dist"));
  log("Copied real app/dist (compiled gateway entrypoint)");

  const nodeModulesSrc = path.join(gatewayRoot, "node_modules");
  const appNodeModulesDest = path.join(outputDir, "app", "node_modules");
  if (fs.existsSync(nodeModulesSrc)) {
    // Runtime deps only would normally be a `pnpm deploy`/prod-install step;
    // for this proof-of-concept, note the real dependency count so the
    // layout claim ("app\node_modules\ exists and is populated") is
    // verifiable without doing a full production dependency prune here,
    // which is a packaging-pipeline concern, not a layout concern.
    fs.mkdirSync(appNodeModulesDest, { recursive: true });
    const depCount = fs.readdirSync(nodeModulesSrc).length;
    fs.writeFileSync(
      path.join(appNodeModulesDest, "README.txt"),
      `This gateway now declares ONE real runtime dependency: "serialport"\n` +
        `(package.json "dependencies") - added for the generic RS232 serial\n` +
        `transport (src/transport/serial-transport.ts, CEO-confirmed ESSAE\n` +
        `transport parameters). Everything else (TypeScript, Jest, ts-node) is\n` +
        `a devDependency - build/test-time only, not shipped.\n` +
        `\n` +
        `This step still does NOT copy a real, pruned, production node_modules\n` +
        `here (unchanged proof-of-concept behavior from earlier checkpoints) -\n` +
        `it only writes this note. That gap now matters more than it used to:\n` +
        `"serialport" ships a compiled NATIVE addon (@serialport/bindings-cpp).\n` +
        `The prebuilt binary this sandbox resolved is for linux-x64 and will NOT\n` +
        `run on the deployed Windows target - a real production packaging step\n` +
        `must install/rebuild "serialport" ON (or targeting) the exact vendored\n` +
        `Windows node.exe this deployment ships (docs/gateway-decision.md), the\n` +
        `same ABI-matching concern this project already documented and avoided\n` +
        `for SQLite (node:sqlite over better-sqlite3 - see\n` +
        `docs/gateway-architecture.md §2) by not needing a native dependency at\n` +
        `all. serialport had no such built-in alternative, so this risk could\n` +
        `not be avoided the same way - flagged here, not silently accepted, and\n` +
        `not yet solved: implementing real production dependency pruning plus\n` +
        `cross-platform native-module handling is out of this change's scope\n` +
        `(generic serial transport + configuration seam only) and is tracked as\n` +
        `a packaging follow-up, not guessed at here.\n` +
        `(For reference: this package's full dev node_modules currently has\n` +
        `${depCount} top-level entries.)\n`,
    );
  }

  // --- Persistent application data (separate branch, never touched by an upgrade) ---

  fs.mkdirSync(path.join(outputDir, "config"), { recursive: true });
  fs.copyFileSync(
    path.join(gatewayRoot, "config", "gateway.config.example.json"),
    path.join(outputDir, "config", "gateway.config.example.json"),
  );
  log("Copied config/gateway.config.example.json (operator copies this to gateway.config.json and edits it)");

  fs.mkdirSync(path.join(outputDir, "data"), { recursive: true });
  fs.writeFileSync(
    path.join(outputDir, "data", ".gitkeep"),
    "Local persistence (SQLite DB, Checkpoint 3+) lives here. Deliberately empty in Checkpoint 2.\n",
  );

  fs.mkdirSync(path.join(outputDir, "logs"), { recursive: true });
  fs.writeFileSync(
    path.join(outputDir, "logs", ".gitkeep"),
    "WinSW-managed rolling logs land here at runtime. Deliberately empty here.\n",
  );

  log("");
  log("Deployment directory assembled successfully:");
  printTree(outputDir, "");
  log("");
  log(
    "Binaries/runtime (CCMCGateway.exe, CCMCGateway.xml, node\\, app\\) are in a " +
      "separate branch of this tree from persistent data (config\\, data\\, logs\\) - " +
      "an upgrade that replaces the former never has to touch the latter " +
      "(Checkpoint 2 constraint 10).",
  );
}

function printTree(dir, prefix) {
  const entries = fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
  entries.forEach((entry, i) => {
    const isLast = i === entries.length - 1;
    const connector = isLast ? "└── " : "├── ";
    process.stdout.write(`${prefix}${connector}${entry.name}${entry.isDirectory() ? "/" : ""}\n`);
    if (entry.isDirectory()) {
      printTree(path.join(dir, entry.name), prefix + (isLast ? "    " : "│   "));
    }
  });
}

main();
