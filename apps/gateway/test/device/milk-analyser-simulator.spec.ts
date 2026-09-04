import { MilkAnalyserSimulator } from "../../src/device/simulators/milk-analyser-simulator";

describe("MilkAnalyserSimulator", () => {
  it("normal reading: returns the exact scripted reading after connect()", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setDefaultReading({ fat: 4.1, snf: 8.6, temperature: 5.2, capturedAt: "2026-01-01T00:00:00.000Z" });

    const reading = await analyser.read();

    expect(reading).toEqual({ fat: 4.1, snf: 8.6, temperature: 5.2, capturedAt: "2026-01-01T00:00:00.000Z" });
  });

  it("repeated reading: the default reading repeats indefinitely", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setDefaultReading({ fat: 4.0, snf: 8.5, temperature: 4.0, capturedAt: "2026-01-01T00:00:00.000Z" });

    const first = await analyser.read();
    const second = await analyser.read();

    expect(first).toEqual(second);
  });

  it("queued readings are consumed once each, in order, before falling back to the default", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setDefaultReading({ fat: 9, snf: 9, temperature: 9, capturedAt: "2026-01-01T00:00:03.000Z" });
    analyser.setNextReadings([
      { fat: 3.5, snf: 8.2, temperature: 3.9, capturedAt: "2026-01-01T00:00:01.000Z" },
      { fat: 4.5, snf: 8.8, temperature: 4.5, capturedAt: "2026-01-01T00:00:02.000Z" },
    ]);

    expect(await analyser.read()).toEqual({ fat: 3.5, snf: 8.2, temperature: 3.9, capturedAt: "2026-01-01T00:00:01.000Z" });
    expect(await analyser.read()).toEqual({ fat: 4.5, snf: 8.8, temperature: 4.5, capturedAt: "2026-01-01T00:00:02.000Z" });
    expect(await analyser.read()).toEqual({ fat: 9, snf: 9, temperature: 9, capturedAt: "2026-01-01T00:00:03.000Z" });
  });

  it("invalid reading: the simulator does not validate its own readings", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setNextReadings([{ fat: -1, snf: -1, temperature: 999, capturedAt: "2026-01-01T00:00:00.000Z" }]);

    const reading = await analyser.read();

    expect(reading).toEqual({ fat: -1, snf: -1, temperature: 999, capturedAt: "2026-01-01T00:00:00.000Z" });
  });

  it("device unavailable: read() before connect() rejects", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    analyser.setDefaultReading({ fat: 4, snf: 8.5, temperature: 4, capturedAt: "2026-01-01T00:00:00.000Z" });

    await expect(analyser.read()).rejects.toThrow(/not connected/);
  });

  it("device unavailable: simulateConnectFailure() then goOffline() both model unavailability distinctly", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    analyser.simulateConnectFailure("analyser offline: power fault");
    await expect(analyser.connect()).rejects.toThrow("analyser offline: power fault");

    await analyser.connect();
    expect(analyser.status()).toEqual({ state: "CONNECTED" });

    analyser.goOffline("lost communication mid-session");
    expect(analyser.status()).toEqual({ state: "ERROR", lastError: "lost communication mid-session" });
    await expect(analyser.read()).rejects.toThrow(/not connected/);
  });

  it("device unavailable: a queued read failure rejects read() while remaining CONNECTED", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.queueReadFailure("optical sensor fault");

    await expect(analyser.read()).rejects.toThrow("optical sensor fault");
    expect(analyser.status()).toEqual({ state: "CONNECTED" });
  });

  it("delayed response: simulateNextReadDelay() delays exactly the next read() call, one-shot", async () => {
    const analyser = new MilkAnalyserSimulator("analyser-1");
    await analyser.connect();
    analyser.setDefaultReading({ fat: 4, snf: 8.5, temperature: 4, capturedAt: "2026-01-01T00:00:00.000Z" });
    analyser.simulateNextReadDelay(30);

    const start = Date.now();
    await analyser.read();
    expect(Date.now() - start).toBeGreaterThanOrEqual(25);

    const secondStart = Date.now();
    await analyser.read();
    expect(Date.now() - secondStart).toBeLessThan(25);
  });

  it("kind is ANALYSER and deviceId is preserved", () => {
    const analyser = new MilkAnalyserSimulator("analyser-south-2");
    expect(analyser.kind).toBe("ANALYSER");
    expect(analyser.deviceId).toBe("analyser-south-2");
  });

  it("simulateRealisticReading is deterministic for a given seed (demo-only helper)", async () => {
    const a = new MilkAnalyserSimulator("analyser-1");
    const b = new MilkAnalyserSimulator("analyser-2");
    await a.connect();
    await b.connect();

    a.simulateRealisticReading(7, "2026-01-01T00:00:00.000Z");
    b.simulateRealisticReading(7, "2026-01-01T00:00:00.000Z");

    expect(await a.read()).toEqual(await b.read());
  });
});
