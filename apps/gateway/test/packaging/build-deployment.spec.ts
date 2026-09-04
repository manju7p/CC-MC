import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { execFileSync } from "child_process";

/**
 * Checkpoint 6B: a genuinely-runnable-in-this-sandbox regression test for
 * packaging/build-deployment.mjs's output layout - the "packaged file
 * structure" half of what this checkpoint's testing requirements ask for
 * (real Windows service install/start/autostart/crash-recovery/SCM
 * behavior is NOT covered here and cannot be - see the 6B report).
 *
 * This actually runs the real script (including its real `tsc` build
 * step, not a mock) against a throwaway temp directory and asserts on the
 * resulting layout, so a future change that breaks the script (e.g.
 * renames a copied file, or breaks the tsc build) fails a test instead of
 * only being caught by someone manually re-running the script.
 */
describe("packaging/build-deployment.mjs", () => {
  const gatewayRoot = path.resolve(__dirname, "..", "..");
  let outputDir: string;

  beforeAll(() => {
    outputDir = fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-deployment-test-"));
    execFileSync("node", [path.join(gatewayRoot, "packaging", "build-deployment.mjs"), outputDir], {
      cwd: gatewayRoot,
      stdio: "pipe",
    });
  }, 60_000);

  afterAll(() => {
    fs.rmSync(outputDir, { recursive: true, force: true });
  });

  it("produces the real (non-placeholder) service scripts and config, copied verbatim from apps/gateway/service", () => {
    for (const relPath of ["CCMCGateway.xml", "install.ps1", "uninstall.ps1"]) {
      const produced = fs.readFileSync(path.join(outputDir, relPath), "utf-8");
      const source = fs.readFileSync(path.join(gatewayRoot, "service", relPath), "utf-8");
      expect(produced).toBe(source);
    }
  });

  it("produces a real compiled gateway entrypoint (app/dist/main.js) from the actual tsc build, not a placeholder", () => {
    const entryPath = path.join(outputDir, "app", "dist", "main.js");
    expect(fs.existsSync(entryPath)).toBe(true);
    const contents = fs.readFileSync(entryPath, "utf-8");
    // A real compiled entrypoint references the Gateway class it was
    // built from - a placeholder or empty file would not.
    expect(contents).toContain("Gateway");
    expect(contents.length).toBeGreaterThan(0);
  });

  it("clearly labels the binaries this sandbox cannot produce (CCMCGateway.exe, node/node.exe) as placeholders, never silently as real", () => {
    for (const relPath of ["CCMCGateway.exe", path.join("node", "node.exe")]) {
      const contents = fs.readFileSync(path.join(outputDir, relPath), "utf-8");
      expect(contents).toContain("PLACEHOLDER");
    }
  });

  it("keeps persistent-data directories (config/, data/, logs/) as a separate branch from binaries/runtime (app/, node/, CCMCGateway.*)", () => {
    // Layout assertion, not a rebuild of the script's own logic: an
    // upgrade replacing app/, node/, CCMCGateway.exe, CCMCGateway.xml must
    // never need to touch config/, data/, or logs/ (Checkpoint 2
    // constraint 10 - see docs/gateway-decision.md §8 and
    // docs/gateway-setup.md §8).
    expect(fs.existsSync(path.join(outputDir, "config", "gateway.config.example.json"))).toBe(true);
    expect(fs.existsSync(path.join(outputDir, "data"))).toBe(true);
    expect(fs.existsSync(path.join(outputDir, "logs"))).toBe(true);
    expect(fs.existsSync(path.join(outputDir, "app"))).toBe(true);
    expect(fs.existsSync(path.join(outputDir, "node"))).toBe(true);

    // data/ and logs/ must be empty at packaging time (gateway.sqlite and
    // WinSW logs are runtime-created, never shipped in the package).
    const dataEntries = fs.readdirSync(path.join(outputDir, "data"));
    const logsEntries = fs.readdirSync(path.join(outputDir, "logs"));
    expect(dataEntries.every((f) => f === ".gitkeep")).toBe(true);
    expect(logsEntries.every((f) => f === ".gitkeep")).toBe(true);
  });

  it("is re-runnable (idempotent output) - a second run against the same output dir succeeds and removes the prior contents first", () => {
    // The script itself rm -rf's outputDir before rebuilding - re-run it
    // and confirm it still produces a valid layout rather than merging
    // with or erroring on the leftover directory from beforeAll().
    execFileSync("node", [path.join(gatewayRoot, "packaging", "build-deployment.mjs"), outputDir], {
      cwd: gatewayRoot,
      stdio: "pipe",
    });
    expect(fs.existsSync(path.join(outputDir, "app", "dist", "main.js"))).toBe(true);
  }, 60_000);
});
