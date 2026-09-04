/**
 * Pure, deterministic mapping from an HTTP response (status + parsed
 * body) to what HttpCloudClient should do next. Deliberately a plain
 * function with no HTTP/fetch/token concerns, so every documented
 * classification rule is independently unit-testable without a real (or
 * mocked) network call - see test/sync/http-response-classification.spec.ts.
 *
 * The checkpoint's required classification, and this function's mapping:
 *   retryable              -> "retryable"      (network errors are
 *                              classified separately by the caller, since
 *                              they never reach here - no HTTP response
 *                              exists yet. This function only classifies
 *                              a response that DID arrive.)
 *   authentication-retryable -> "auth-retryable" (HTTP 401)
 *   terminal                -> "terminal"       (HTTP 409 payload
 *                              conflict, and any other non-retryable 4xx)
 *   idempotent-success      -> "success" with outcome "duplicate"
 *   (plain success)         -> "success" with outcome "created"
 *
 * IMPORTANT: created vs. duplicate is read from the response BODY's
 * `outcome` field (ReceptionCreateResult, apps/api/src/reception/reception.service.ts),
 * NOT inferred from the HTTP status code. Both are HTTP 201 - the cloud
 * API deliberately keeps POST /reception's status code exactly what it
 * always was (Nest's default 201 for POST) whether the row was freshly
 * inserted or returned as an idempotent replay, so the existing web
 * client's `res.status === 201` assumption never had to change. This was
 * found the hard way: an earlier version of this function inferred
 * outcome from status (201 -> created, 200 -> duplicate), which happened
 * to make test/cloud-integration/concurrent-duplicate.spec.ts's
 * "same key + same payload" case report BOTH concurrent requests as
 * "created" - correct row count (the DB constraint doesn't lie), but a
 * wrong signal to whoever reads CloudSendResult.outcome. Falls back to
 * the old status-code heuristic only if the body has no `outcome` field
 * at all, so this stays robust against a response that doesn't carry it.
 */
export type ClassifiedResponse =
  | { kind: "success"; outcome: "created" | "duplicate"; cloudTransactionId: number }
  | { kind: "auth-retryable" }
  | { kind: "retryable"; message: string }
  | { kind: "terminal"; message: string };

export function classifyHttpResponse(status: number, body: unknown): ClassifiedResponse {
  if (status === 201 || status === 200) {
    const id = extractId(body);
    if (id === null) {
      // The cloud said success but didn't hand back an id to persist as
      // cloudTransactionId - treat as terminal rather than silently
      // marking the outbox item SYNCED with no way to ever know the
      // cloud-side id. This should be unreachable against the real API
      // (reception.service.ts always returns the full transaction), so
      // reaching it means something is badly wrong, not worth retrying.
      return { kind: "terminal", message: `Cloud returned HTTP ${status} with no numeric "id" in the response body` };
    }
    const bodyOutcome = extractOutcome(body);
    const outcome = bodyOutcome ?? (status === 201 ? "created" : "duplicate");
    return { kind: "success", outcome, cloudTransactionId: id };
  }

  if (status === 401) {
    return { kind: "auth-retryable" };
  }

  if (status === 409) {
    // A payload conflict from ReceptionService.createIdempotent() - the
    // SAME localIdempotencyKey was already used for different data.
    // Retrying this exact payload again will never succeed - terminal,
    // not retryable, per the checkpoint's explicit
    // "A payload conflict is terminal failure."
    return { kind: "terminal", message: extractMessage(body) ?? "Cloud reported an idempotency payload conflict (HTTP 409)" };
  }

  if (status === 429 || (status >= 500 && status <= 599)) {
    // 429 (rate limited) and 5xx (server-side failure) are both
    // conditions expected to clear on their own - safe and appropriate
    // to retry with backoff.
    return { kind: "retryable", message: extractMessage(body) ?? `Cloud returned retryable HTTP ${status}` };
  }

  // Every other 4xx (400 validation, 403 forbidden/centre-scope denial,
  // 404 not found, 422, etc.) - retrying the exact same payload/request
  // will get the exact same rejection every time. Terminal, not
  // retryable, so the outbox item goes to FAILED for ops review instead
  // of burning attempts forever against a request that can never succeed.
  return { kind: "terminal", message: extractMessage(body) ?? `Cloud returned non-retryable HTTP ${status}` };
}

function extractOutcome(body: unknown): "created" | "duplicate" | null {
  if (body && typeof body === "object" && "outcome" in body) {
    const outcome = (body as { outcome: unknown }).outcome;
    if (outcome === "created" || outcome === "duplicate") return outcome;
  }
  return null;
}

function extractId(body: unknown): number | null {
  if (body && typeof body === "object" && "id" in body) {
    const id = (body as { id: unknown }).id;
    if (typeof id === "number" && Number.isFinite(id)) return id;
  }
  return null;
}

function extractMessage(body: unknown): string | null {
  if (!body || typeof body !== "object" || !("message" in body)) return null;
  const message = (body as { message: unknown }).message;
  if (typeof message === "string") return message;
  if (Array.isArray(message)) return message.filter((m) => typeof m === "string").join("; ") || null;
  return null;
}
