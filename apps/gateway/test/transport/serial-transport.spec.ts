/**
 * Tests for `SerialTransport` (src/transport/serial-transport.ts).
 *
 * These use `SerialPortMock` from the real `serialport` package (backed by
 * `@serialport/binding-mock`), NOT a hand-rolled fake - so what's actually
 * exercised is the real `serialport` API surface (open/close/isOpen/
 * on('data')/on('error')) with a virtual port standing in for real
 * hardware. This proves configuration, connection lifecycle, and failure
 * handling genuinely - it does NOT and cannot prove anything about real
 * ESSAE hardware or the ESSAE message protocol, which remains
 * unimplemented (see serial-transport.ts's header comment and
 * docs/gateway-architecture.md §15).
 *
 * Per the project's own directive: "Transport configuration is known;
 * protocol semantics remain unverified." These tests verify the former
 * only.
 */
import { SerialPortMock } from "serialport";
import { MockBinding } from "@serialport/binding-mock";
import { SerialTransport } from "../../src/transport/serial-transport";
import { ESSAE_CONFIRMED_SERIAL_PARAMETERS } from "../../src/transport/serial-transport.types";
import { Logger } from "../../src/logging/logger";

// Silence structured JSON-line logs during this suite - they're asserted
// on directly where relevant, and are otherwise just noise in test output.
const silentLogger = new Logger("test.serial-transport");
silentLogger.info = jest.fn();
silentLogger.warn = jest.fn();
silentLogger.error = jest.fn();
silentLogger.debug = jest.fn();

function freshPortPath(): string {
  return `/dev/mock-essae-${Math.random().toString(36).slice(2)}`;
}

describe("SerialTransport", () => {
  afterEach(() => {
    MockBinding.reset();
  });

  it("opens the underlying port with the CEO-confirmed transport parameters, not guessed values", async () => {
    const portPath = freshPortPath();
    MockBinding.createPort(portPath, {});
    const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);

    await transport.open();

    // Read back the ACTUAL options the mock port was opened with - not
    // just what we intended to pass - proving the confirmed parameters
    // really reached the underlying port, not just the constructor args.
    const mockPort = (transport as any).port as InstanceType<typeof SerialPortMock>;
    expect(mockPort.port!.openOptions.baudRate).toBe(9600);
    expect(mockPort.port!.openOptions.dataBits).toBe(ESSAE_CONFIRMED_SERIAL_PARAMETERS.dataBits);
    expect(mockPort.port!.openOptions.parity).toBe("none");
    expect(mockPort.port!.openOptions.stopBits).toBe(ESSAE_CONFIRMED_SERIAL_PARAMETERS.stopBits);
    // No flow control: neither hardware (RTS/CTS) nor software (XON/XOFF).
    expect(mockPort.port!.openOptions.rtscts).toBe(false);
    expect(mockPort.port!.openOptions.xon).toBe(false);
    expect(mockPort.port!.openOptions.xoff).toBe(false);
    expect(mockPort.port!.openOptions.xany).toBe(false);

    await transport.close();
  });

  it("respects a configurable baud rate - the one CEO-confirmed-configurable parameter", async () => {
    const portPath = freshPortPath();
    MockBinding.createPort(portPath, {});
    // A different baud rate than the ESSAE default, proving this is a
    // real configuration value, not a hardcoded constant.
    const transport = new SerialTransport({ portPath, baudRate: 19200 }, silentLogger, SerialPortMock as any);

    await transport.open();

    const mockPort = (transport as any).port as InstanceType<typeof SerialPortMock>;
    expect(mockPort.port!.openOptions.baudRate).toBe(19200);

    await transport.close();
  });

  describe("connection lifecycle", () => {
    it("isOpen() reflects DISCONNECTED before open() is called", () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);
      expect(transport.isOpen()).toBe(false);
    });

    it("isOpen() reflects CONNECTED after a successful open()", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);

      await transport.open();

      expect(transport.isOpen()).toBe(true);
      await transport.close();
    });

    it("delivers raw bytes received from the device to the registered onData handler, unparsed", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);
      const received: Buffer[] = [];
      transport.onData((chunk) => received.push(chunk));

      await transport.open();
      const mockPort = (transport as any).port as InstanceType<typeof SerialPortMock>;
      mockPort.port!.emitData(Buffer.from([0x02, 0x39, 0x36, 0x30, 0x30, 0x03])); // arbitrary raw bytes - NOT a claimed ESSAE frame

      // Let the 'data' event's microtask/event-loop turn flush.
      await new Promise((resolve) => setImmediate(resolve));

      expect(received).toHaveLength(1);
      expect(received[0]).toEqual(Buffer.from([0x02, 0x39, 0x36, 0x30, 0x30, 0x03]));

      await transport.close();
    });

    it("close() transitions isOpen() back to false and is safe to call twice", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);

      await transport.open();
      await transport.close();
      expect(transport.isOpen()).toBe(false);

      // Per the Transport interface's contract: "Safe to call even if
      // already closed."
      await expect(transport.close()).resolves.toBeUndefined();
    });
  });

  describe("failure handling", () => {
    it("open() rejects when the underlying serial port does not exist, and isOpen() stays false", async () => {
      const portPath = freshPortPath(); // deliberately never registered via MockBinding.createPort
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);

      await expect(transport.open()).rejects.toThrow();
      expect(transport.isOpen()).toBe(false);
    });

    it("logs a serial port error without crashing the process (no unhandled 'error' event)", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);
      await transport.open();

      const mockPort = (transport as any).port as InstanceType<typeof SerialPortMock>;
      // Simulate a real serial-level error (e.g. a driver-level failure)
      // without going through disconnect() - proves this doesn't throw
      // an unhandled exception and crash the process.
      expect(() => mockPort.emit("error", new Error("simulated driver error"))).not.toThrow();
      expect(silentLogger.error).toHaveBeenCalledWith(
        "Serial port error",
        expect.objectContaining({ portPath, error: "simulated driver error" }),
      );

      await transport.close();
    });
  });

  describe("disconnect/reconnect", () => {
    it("can be closed and reopened on the same transport instance", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);

      await transport.open();
      expect(transport.isOpen()).toBe(true);

      await transport.close();
      expect(transport.isOpen()).toBe(false);

      await transport.open();
      expect(transport.isOpen()).toBe(true);

      await transport.close();
    });

    it("does not deliver a chunk to onData more than once after a reconnect (no duplicate listener stacking)", async () => {
      const portPath = freshPortPath();
      MockBinding.createPort(portPath, {});
      const transport = new SerialTransport({ portPath, baudRate: 9600 }, silentLogger, SerialPortMock as any);
      const received: Buffer[] = [];
      transport.onData((chunk) => received.push(chunk));

      await transport.open();
      await transport.close();
      await transport.open(); // reconnect - the same underlying port instance is reused

      const mockPort = (transport as any).port as InstanceType<typeof SerialPortMock>;
      mockPort.port!.emitData(Buffer.from("x"));
      await new Promise((resolve) => setImmediate(resolve));

      expect(received).toHaveLength(1);

      await transport.close();
    });
  });
});
