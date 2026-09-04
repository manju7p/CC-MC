import { istTodayBoundsUtc } from "../../src/local-api/local-day-bounds";

describe("istTodayBoundsUtc", () => {
  it("computes IST midnight as the UTC instant 5:30 behind local midnight", () => {
    // 2026-08-23 12:00:00 UTC = 2026-08-23 17:30:00 IST - well inside the
    // 23rd's IST calendar day.
    const now = new Date("2026-08-23T12:00:00.000Z");
    const bounds = istTodayBoundsUtc(now);

    expect(bounds.dateLabel).toBe("2026-08-23");
    // IST midnight (2026-08-23 00:00 IST) is 2026-08-22 18:30 UTC.
    expect(bounds.start).toBe("2026-08-22T18:30:00.000Z");
    expect(bounds.end).toBe("2026-08-23T18:30:00.000Z");
  });

  it("a UTC instant just after IST midnight still reports the new IST day, not the old UTC day", () => {
    // 2026-08-22 19:00:00 UTC = 2026-08-23 00:30:00 IST - just past IST
    // midnight into the 23rd, even though the UTC calendar date is still
    // the 22nd. This is the exact case the doc comment warns about.
    const now = new Date("2026-08-22T19:00:00.000Z");
    const bounds = istTodayBoundsUtc(now);

    expect(bounds.dateLabel).toBe("2026-08-23");
    expect(bounds.start).toBe("2026-08-22T18:30:00.000Z");
    expect(bounds.end).toBe("2026-08-23T18:30:00.000Z");
  });

  it("a UTC instant just before IST midnight reports the previous IST day", () => {
    // 2026-08-22 18:00:00 UTC = 2026-08-22 23:30:00 IST - still the 22nd in IST.
    const now = new Date("2026-08-22T18:00:00.000Z");
    const bounds = istTodayBoundsUtc(now);

    expect(bounds.dateLabel).toBe("2026-08-22");
    expect(bounds.start).toBe("2026-08-21T18:30:00.000Z");
    expect(bounds.end).toBe("2026-08-22T18:30:00.000Z");
  });

  it("end is always exactly 24 hours after start", () => {
    const bounds = istTodayBoundsUtc(new Date("2026-01-01T00:00:00.000Z"));
    const diffMs = new Date(bounds.end).getTime() - new Date(bounds.start).getTime();
    expect(diffMs).toBe(24 * 60 * 60 * 1000);
  });
});
