import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { Gateway } from "../src/gateway";
import { loadConfig, resolveConfigPath, ConfigValidationError } from "../src/config/config.loader";
import { Logger } from "../src/logging/logger";
import { DeviceManager } from "../src/device/device-manager";
import type { Device, DeviceStatus } from "../src/device/device.types";
import type { GatewayConfig } from "../src/config/config.types";

// Silence structured log output during tests - it's stdout noise for a
// passing test run, not something assertions depend on.
function silentLogger(): Logger {
  const logger = new Logger("test");
  jest.spyOn(process.stdout, "write").mockImplementation(() => true);
  return logger;
}

describe("Gateway lifecycle", () => {
  // Checkpoint 3 wires a real SqliteLocalStorage into Gateway.start()/
  // stop() (see gateway.ts), so these tests now touch a real SQLite file
  // on disk - a fresh temp data directory per test keeps them isolated
  // and self-cleaning, same pattern as the config.loader tests below.
  let tmpDir: string;
  let testConfig: GatewayConfig;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-lifecycle-"));
    testConfig = {
      gatewayId: "gw-test-001",
      centreId: 42,
      cloudApiBaseUrl: "https://example.invalid",
      logDirectory: path.join(tmpDir, "logs"),
      dataDirectory: path.join(tmpDir, "data"),
      cloudAuthEmail: "gateway-test@ccmc.local",
      cloudAuthPassword: "test-only-password-never-real",
    };
  });

  afterEach(() => {
    jest.restoreAllMocks();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it("starts in STOPPED state before start() is called", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    const snapshot = await gateway.getHealthSnapshot();
    expect(snapshot.serviceState).toBe("STOPPED");
    expect(snapshot.startedAt).toBeNull();
    expect(snapshot.uptimeSeconds).toBe(0);
  });

  it("transitions to RUNNING after start(), and reports gatewayId/centreId/version", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    await gateway.start();

    const snapshot = await gateway.getHealthSnapshot();
    expect(snapshot.serviceState).toBe("RUNNING");
    expect(snapshot.gatewayId).toBe("gw-test-001");
    expect(snapshot.centreId).toBe(42);
    expect(snapshot.version).toBe("0.1.0");
    expect(snapshot.startedAt).not.toBeNull();
    expect(typeof snapshot.uptimeSeconds).toBe("number");

    await gateway.stop();
  });

  it("reports cloud/device connectivity honestly as UNKNOWN, and a real pendingSyncCount (0 with no transactions)", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    await gateway.start();

    const snapshot = await gateway.getHealthSnapshot();
    expect(snapshot.cloudConnectivity).toBe("UNKNOWN");
    expect(snapshot.deviceConnectivity).toBe("UNKNOWN");
    expect(snapshot.pendingSyncCount).toBe(0);

    await gateway.stop();
  });

  it("transitions to STOPPED after stop(), and clears startedAt/uptime", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    await gateway.start();
    await gateway.stop();

    const snapshot = await gateway.getHealthSnapshot();
    expect(snapshot.serviceState).toBe("STOPPED");
    expect(snapshot.startedAt).toBeNull();
    expect(snapshot.uptimeSeconds).toBe(0);
  });

  it("uptimeSeconds never goes negative and does not reset between reads", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    await gateway.start();
    const first = (await gateway.getHealthSnapshot()).uptimeSeconds;
    const second = (await gateway.getHealthSnapshot()).uptimeSeconds;
    expect(first).toBeGreaterThanOrEqual(0);
    expect(second).toBeGreaterThanOrEqual(first);
    await gateway.stop();
  });

  it("creates the SQLite database file under dataDirectory on start()", async () => {
    const gateway = new Gateway(testConfig, "0.1.0", silentLogger());
    await gateway.start();
    expect(fs.existsSync(path.join(testConfig.dataDirectory, "gateway.sqlite"))).toBe(true);
    await gateway.stop();
  });
});

describe("config.loader", () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "ccmc-gateway-test-"));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it("loads a valid config file", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
        dataDirectory: "./data",
        cloudAuthEmail: "gateway-blr-cc-01@ccmc.local",
        cloudAuthPassword: "some-password",
      }),
    );

    const config = loadConfig(configPath);
    expect(config.gatewayId).toBe("gw-001");
    expect(config.centreId).toBe(7);
    expect(config.dataDirectory).toBe("./data");
    expect(config.cloudAuthEmail).toBe("gateway-blr-cc-01@ccmc.local");
    expect(config.cloudAuthPassword).toBe("some-password");
    // No "serial" key at all is a valid config - most current deployments
    // still run purely against simulator devices with no real serial
    // hardware wired up (see GatewayConfig.serial's doc comment).
    expect(config.serial).toBeUndefined();
  });

  it("loads a valid config file that includes the optional serial block (CEO-confirmed ESSAE transport parameters)", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
        dataDirectory: "./data",
        cloudAuthEmail: "gateway-blr-cc-01@ccmc.local",
        cloudAuthPassword: "some-password",
        serial: { portPath: "COM3", baudRate: 9600 },
      }),
    );

    const config = loadConfig(configPath);
    expect(config.serial).toEqual({ portPath: "COM3", baudRate: 9600 });
  });

  it("throws ConfigValidationError when serial is present but missing portPath", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
        dataDirectory: "./data",
        cloudAuthEmail: "gateway-blr-cc-01@ccmc.local",
        cloudAuthPassword: "some-password",
        serial: { baudRate: 9600 },
      }),
    );
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
    expect(() => loadConfig(configPath)).toThrow(/portPath/);
  });

  it("throws ConfigValidationError when serial.baudRate is not an integer", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
        dataDirectory: "./data",
        cloudAuthEmail: "gateway-blr-cc-01@ccmc.local",
        cloudAuthPassword: "some-password",
        serial: { portPath: "COM3", baudRate: "fast" },
      }),
    );
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
    expect(() => loadConfig(configPath)).toThrow(/baudRate/);
  });

  it("throws ConfigValidationError when cloudAuthEmail is missing (Checkpoint 5)", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
        dataDirectory: "./data",
        cloudAuthPassword: "some-password",
      }),
    );
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
    expect(() => loadConfig(configPath)).toThrow(/cloudAuthEmail/);
  });

  it("throws ConfigValidationError when dataDirectory is missing", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: 7,
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
      }),
    );
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
    expect(() => loadConfig(configPath)).toThrow(/dataDirectory/);
  });

  it("throws ConfigValidationError when the file does not exist", () => {
    expect(() => loadConfig(path.join(tmpDir, "does-not-exist.json"))).toThrow(ConfigValidationError);
  });

  it("throws ConfigValidationError when the file is not valid JSON", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(configPath, "{ not valid json");
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
  });

  it("throws ConfigValidationError when a required field is missing", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(configPath, JSON.stringify({ gatewayId: "gw-001" }));
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
    expect(() => loadConfig(configPath)).toThrow(/centreId/);
  });

  it("throws ConfigValidationError when centreId is not an integer", () => {
    const configPath = path.join(tmpDir, "gateway.config.json");
    fs.writeFileSync(
      configPath,
      JSON.stringify({
        gatewayId: "gw-001",
        centreId: "not-a-number",
        cloudApiBaseUrl: "https://api.example.org",
        logDirectory: "./logs",
      }),
    );
    expect(() => loadConfig(configPath)).toThrow(ConfigValidationError);
  });

  it("resolveConfigPath honors GATEWAY_CONFIG_PATH when set", () => {
    const resolved = resolveConfigPath({ GATEWAY_CONFIG_PATH: "/custom/path/gateway.config.json" } as NodeJS.ProcessEnv);
    expect(resolved).toBe("/custom/path/gateway.config.json");
  });

  it("resolveConfigPath falls back to a relative default when unset", () => {
    const resolved = resolveConfigPath({} as NodeJS.ProcessEnv);
    expect(resolved).toContain(path.join("config", "gateway.config.json"));
  });
});

describe("DeviceManager", () => {
  function fakeDevice(deviceId: string): Device<{ value: number }> & {
    connectCalls: number;
    disconnectCalls: number;
  } {
    let state: DeviceStatus = { state: "DISCONNECTED" };
    return {
      kind: "SCALE",
      deviceId,
      connectCalls: 0,
      disconnectCalls: 0,
      async connect() {
        this.connectCalls++;
        state = { state: "CONNECTED" };
      },
      async disconnect() {
        this.disconnectCalls++;
        state = { state: "DISCONNECTED" };
      },
      status() {
        return state;
      },
      async read() {
        return { value: 1 };
      },
    };
  }

  it("registers and lists devices", () => {
    const manager = new DeviceManager(new Logger("test"));
    const device = fakeDevice("scale-1");
    manager.register(device);
    expect(manager.list()).toHaveLength(1);
    expect(manager.list()[0].deviceId).toBe("scale-1");
  });

  it("throws when registering a duplicate deviceId", () => {
    const manager = new DeviceManager(new Logger("test"));
    manager.register(fakeDevice("scale-1"));
    expect(() => manager.register(fakeDevice("scale-1"))).toThrow(/already registered/);
  });

  it("unregisters a device", () => {
    const manager = new DeviceManager(new Logger("test"));
    manager.register(fakeDevice("scale-1"));
    manager.unregister("scale-1");
    expect(manager.list()).toHaveLength(0);
  });

  it("connectAll() connects every registered device", async () => {
    const manager = new DeviceManager(new Logger("test"));
    const a = fakeDevice("scale-1");
    const b = fakeDevice("analyser-1");
    manager.register(a);
    manager.register(b);

    await manager.connectAll();

    expect(a.connectCalls).toBe(1);
    expect(b.connectCalls).toBe(1);
    expect(a.status().state).toBe("CONNECTED");
    expect(b.status().state).toBe("CONNECTED");
  });

  it("disconnectAll() disconnects every registered device", async () => {
    const manager = new DeviceManager(new Logger("test"));
    const a = fakeDevice("scale-1");
    manager.register(a);
    await manager.connectAll();

    await manager.disconnectAll();

    expect(a.disconnectCalls).toBe(1);
    expect(a.status().state).toBe("DISCONNECTED");
  });

  it("connectAll() with zero registered devices resolves without error (Checkpoint 2 default state)", async () => {
    const manager = new DeviceManager(new Logger("test"));
    await expect(manager.connectAll()).resolves.toBeUndefined();
  });
});
