import serial
import time

PORT = "COM4"
BAUDRATE = 2400

ser = serial.Serial(
    port=PORT,
    baudrate=BAUDRATE,
    bytesize=serial.EIGHTBITS,
    parity=serial.PARITY_NONE,
    stopbits=serial.STOPBITS_ONE,
    timeout=0.1,
    xonxoff=False,
    rtscts=False,
    dsrdtr=False,
)

print(f"OPENED {PORT} @ {BAUDRATE} 8N1")
print("Capturing for 10 seconds...")
print("Interact with the weighing machine during this time.")

captured = bytearray()

start = time.time()

try:
    while time.time() - start < 10:
        data = ser.read(4096)

        if data:
            captured.extend(data)

finally:
    ser.close()

with open("serial_capture.bin", "wb") as f:
    f.write(captured)

print()
print(f"Captured {len(captured)} bytes.")
print("Saved to serial_capture.bin")

print()
print("HEX:")
print(" ".join(f"{b:02X}" for b in captured[:500]))

print()
print("ASCII:")
print(captured[:500].decode("ascii", errors="replace"))