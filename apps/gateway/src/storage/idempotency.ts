import * as crypto from "crypto";

/**
 * Generates a new local idempotency key. Call this exactly ONCE per
 * logical transaction, at the point where the gateway first decides "this
 * is a complete reception I need to persist" (Checkpoint 4's capture
 * flow). The caller holds onto the returned key and passes the SAME key
 * into every retried call to LocalStorage.createLocalTransaction() for
 * that same logical transaction - a retry must never call this function
 * again, or it would defeat the entire dedup mechanism.
 *
 * Uses crypto.randomUUID() (Node's built-in CSPRNG-backed UUID v4, no
 * extra dependency - Rule 11: boring, reliable technology) prefixed with
 * the gateway ID. The UUID alone is already collision-resistant enough on
 * its own; the gatewayId prefix is not needed for uniqueness, but makes a
 * key immediately traceable to its originating gateway installation when
 * an operator is reading raw outbox/cloud data during troubleshooting -
 * a debuggability win at effectively zero cost.
 */
export function generateLocalIdempotencyKey(gatewayId: string): string {
  return `${gatewayId}:${crypto.randomUUID()}`;
}
