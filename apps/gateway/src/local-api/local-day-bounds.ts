/**
 * IST (UTC+5:30) calendar-day bounds, reused verbatim from the cloud
 * API's own convention (apps/api/src/dashboard/dashboard.service.ts's
 * istDayBoundsUtc(), docs/assumptions.md #dashboard-day-boundary) so the
 * local gateway's "today" figure means exactly the same calendar day as
 * the cloud dashboard's "today" figure - the BRD's deployment context is
 * Indian dairy chilling centres, not the server's (or this gateway
 * machine's) own OS timezone. Hardcoded rather than pulling in a
 * timezone library, per Rule 4/Rule 11 (boring, minimal deps) - same
 * trade-off the cloud side already made.
 *
 * Deliberately a small standalone copy rather than an import from
 * apps/api: the gateway package has no dependency on apps/api today (and
 * should not gain one just for an 8-line date calculation), and
 * @cc-mc/shared-types is for cross-app DTOs, not internal helper
 * functions. If this logic needs to change, both copies must be updated
 * together - a note left here and in dashboard.service.ts.
 */
const IST_OFFSET_MS = 5.5 * 60 * 60 * 1000;

export interface IstDayBounds {
  /** ISO-8601 UTC instant marking the start of "today" in IST. */
  start: string;
  /** ISO-8601 UTC instant marking the start of "tomorrow" in IST (half-open range end). */
  end: string;
  /** The IST calendar date this range represents, e.g. "2026-08-23". */
  dateLabel: string;
}

export function istTodayBoundsUtc(now: Date): IstDayBounds {
  const shifted = new Date(now.getTime() + IST_OFFSET_MS);
  const istMidnightAsUtc = Date.UTC(shifted.getUTCFullYear(), shifted.getUTCMonth(), shifted.getUTCDate());
  const start = new Date(istMidnightAsUtc - IST_OFFSET_MS);
  const end = new Date(start.getTime() + 24 * 60 * 60 * 1000);
  // Same reasoning as dashboard.service.ts: `start` is a UTC instant whose
  // own ISO date can land on the previous UTC calendar day even though it
  // marks the start of *today* in IST, so the label is derived from the
  // IST-shifted clock face, not from `start` itself.
  const dateLabel = shifted.toISOString().slice(0, 10);
  return { start: start.toISOString(), end: end.toISOString(), dateLabel };
}
