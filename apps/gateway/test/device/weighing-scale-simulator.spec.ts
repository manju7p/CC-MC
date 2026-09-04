import { WeighingScaleSimulator } from "../../src/device/simulators/weighing-scale-simulator";

describe("WeighingScaleSimulator", () => {
  it("normal reading: returns the exact scripted reading after connect()", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 1250.5, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });

    const reading = await scale.read();

    expect(reading).toEqual({ weightKg: 1250.5, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });
    expect(scale.status()).toEqual({ state: "CONNECTED" });
  });

  it("repeated reading: the default reading repeats indefinitely across multiple read() calls", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 500, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });

    const first = await scale.read();
    const second = await scale.read();
    const third = await scale.read();

    expect(first).toEqual(second);
    expect(second).toEqual(third);
  });

  it("queued readings are consumed once each, in order, before falling back to the default", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 999, stable: true, capturedAt: "2026-01-01T00:00:03.000Z" });
    scale.setNextReadings([
      { weightKg: 100, stable: false, capturedAt: "2026-01-01T00:00:01.000Z" },
      { weightKg: 200, stable: true, capturedAt: "2026-01-01T00:00:02.000Z" },
    ]);

    expect(await scale.read()).toEqual({ weightKg: 100, stable: false, capturedAt: "2026-01-01T00:00:01.000Z" });
    expect(await scale.read()).toEqual({ weightKg: 200, stable: true, capturedAt: "2026-01-01T00:00:02.000Z" });
    expect(await scale.read()).toEqual({ weightKg: 999, stable: true, capturedAt: "2026-01-01T00:00:03.000Z" });
  });

  it("invalid reading: the simulator does not validate its own readings - it returns exactly what it's told, even if semantically bad", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setNextReadings([{ weightKg: -50, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" }]);

    const reading = await scale.read();

    expect(reading.weightKg).toBe(-50);
  });

  it("device unavailable: read() before connect() rejects and never touches the outcome queue", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    scale.setDefaultReading({ weightKg: 100, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });

    await expect(scale.read()).rejects.toThrow(/not connected/);
  });

  it("device unavailable: simulateConnectFailure() makes the next connect() reject, then a later connect() can still succeed", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    scale.simulateConnectFailure("scale offline: no response");

    await expect(scale.connect()).rejects.toThrow("scale offline: no response");
    expect(scale.status().state).toBe("ERROR");
    expect(scale.status().lastError).toBe("scale offline: no response");

    // One-shot: a subsequent connect() succeeds (device came back online).
    await scale.connect();
    expect(scale.status()).toEqual({ state: "CONNECTED" });
  });

  it("device unavailable: goOffline() forces a currently-connected device into ERROR immediately", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 100, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });

    scale.goOffline("connection dropped");

    expect(scale.status()).toEqual({ state: "ERROR", lastError: "connection dropped" });
    await expect(scale.read()).rejects.toThrow(/not connected/);
  });

  it("device unavailable: a queued read failure rejects read() while remaining CONNECTED (a sensor fault, not a disconnect)", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.queueReadFailure("sensor jam");

    await expect(scale.read()).rejects.toThrow("sensor jam");
    expect(scale.status()).toEqual({ state: "CONNECTED" });
  });

  it("delayed response: simulateNextReadDelay() delays exactly the next read() call, one-shot", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 100, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });
    scale.simulateNextReadDelay(30);

    const start = Date.now();
    await scale.read();
    const firstElapsed = Date.now() - start;
    expect(firstElapsed).toBeGreaterThanOrEqual(25);

    const secondStart = Date.now();
    await scale.read();
    const secondElapsed = Date.now() - secondStart;
    expect(secondElapsed).toBeLessThan(25);
  });

  it("disconnect() returns the device to DISCONNECTED and read() rejects again", async () => {
    const scale = new WeighingScaleSimulator("scale-1");
    await scale.connect();
    scale.setDefaultReading({ weightKg: 100, stable: true, capturedAt: "2026-01-01T00:00:00.000Z" });
    await scale.read();

    await scale.disconnect();

    expect(scale.status()).toEqual({ state: "DISCONNECTED" });
    await expect(scale.read()).rejects.toThrow(/not connected/);
  });

  it("kind is SCALE and deviceId is preserved", () => {
    const scale = new WeighingScaleSimulator("scale-north-1");
    expect(scale.kind).toBe("SCALE");
    expect(scale.deviceId).toBe("scale-north-1");
  });

  it("simulateRealisticReading is deterministic for a given seed (demo-only helper)", async () => {
    const a = new WeighingScaleSimulator("scale-1");
    const b = new WeighingScaleSimulator("scale-2");
    await a.connect();
    await b.connect();

    a.simulateRealisticReading(42, "2026-01-01T00:00:00.000Z");
    b.simulateRealisticReading(42, "2026-01-01T00:00:00.000Z");

    expect(await a.read()).toEqual(await b.read());
  });
});
