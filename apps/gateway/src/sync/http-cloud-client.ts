import { Logger } from "../logging/logger";
import { CloudAuthTokenCache } from "./cloud-auth";
import { classifyHttpResponse } from "./http-response-classification";
import type { CloudClient, CloudSendResult, ReceptionSyncPayload } from "./cloud-client.types";

export interface HttpCloudClientOptions {
  /** Aborts a single HTTP request (login or sendReception) that takes longer than this - classified as a retryable timeout. */
  requestTimeoutMs: number;
}

export const DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS: HttpCloudClientOptions = { requestTimeoutMs: 10_000 };

/**
 * The real HTTP implementation of CloudClient (Checkpoint 5) -
 * SqliteSyncEngine talks only to the CloudClient interface (cloud-client.types.ts)
 * and has no idea this class, `fetch`, HTTP status codes, or JSON exist.
 * That boundary is what let Checkpoint 3 build and fully test SyncEngine
 * against a fake before any real HTTP code was written, and lets this
 * class change its transport details later without touching SyncEngine.
 *
 * Uses Node's built-in global `fetch` (stable since Node 18, no
 * "--experimental-fetch" flag needed on the Node 22 this project vendors)
 * - no new npm dependency, matching Rule 11 (boring/minimal deps) and the
 * same reasoning that chose node:sqlite over better-sqlite3 in
 * Checkpoint 3.
 *
 * Never logs: the password, the JWT/access token, or the Authorization
 * header value. Every log line here logs only IDs (localIdempotencyKey),
 * HTTP status codes, and booleans - see each log call below.
 */
export class HttpCloudClient implements CloudClient {
  private readonly tokenCache: CloudAuthTokenCache;

  constructor(
    private readonly baseUrl: string,
    email: string,
    password: string,
    private readonly logger: Logger = new Logger("http-cloud-client"),
    private readonly options: HttpCloudClientOptions = DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS,
    private readonly fetchFn: typeof fetch = fetch,
    tokenCache?: CloudAuthTokenCache,
  ) {
    this.tokenCache =
      tokenCache ??
      new CloudAuthTokenCache(() => this.login(email, password), this.logger.child("auth"));
  }

  private async login(email: string, password: string): Promise<{ accessToken: string }> {
    let response: Response;
    try {
      response = await this.fetchWithTimeout(`${this.baseUrl}/auth/login`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ email, password }),
      });
    } catch (err) {
      // Deliberately no email/password in this message - only the
      // network-level failure reason.
      throw new Error(`Cloud login request failed: ${describeNetworkError(err)}`);
    }

    if (!response.ok) {
      // Deliberately no request/response body logged here either - a
      // failed login attempt's body could theoretically echo back
      // request data depending on the server's error format, and this
      // message may end up in gateway logs.
      throw new Error(`Cloud login failed with HTTP ${response.status}`);
    }

    const json = (await response.json().catch(() => null)) as { accessToken?: unknown } | null;
    if (!json || typeof json.accessToken !== "string") {
      throw new Error("Cloud login response did not include an accessToken");
    }
    return { accessToken: json.accessToken };
  }

  async sendReception(payload: ReceptionSyncPayload): Promise<CloudSendResult> {
    return this.attempt(payload, /* isAuthRetry */ false);
  }

  private async attempt(payload: ReceptionSyncPayload, isAuthRetry: boolean): Promise<CloudSendResult> {
    let token: string;
    try {
      token = await this.tokenCache.getToken();
    } catch (err) {
      const message = `Failed to obtain cloud auth token: ${(err as Error).message}`;
      this.logger.warn("Sync request could not obtain an auth token", {
        localIdempotencyKey: payload.localIdempotencyKey,
      });
      return { outcome: "retryable-error", message };
    }

    this.logger.info("Sync request started", {
      localIdempotencyKey: payload.localIdempotencyKey,
      authRetry: isAuthRetry,
    });

    let response: Response;
    try {
      response = await this.fetchWithTimeout(`${this.baseUrl}/reception`, {
        method: "POST",
        headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
        body: JSON.stringify(payload),
      });
    } catch (err) {
      const message = describeNetworkError(err);
      this.logger.warn("Sync request failed before a response was received (network)", {
        localIdempotencyKey: payload.localIdempotencyKey,
        message,
      });
      return { outcome: "retryable-error", message };
    }

    const body: unknown = await response.json().catch(() => null);
    const classified = classifyHttpResponse(response.status, body);

    if (classified.kind === "auth-retryable") {
      if (isAuthRetry) {
        // Already retried once after invalidating the cached token and
        // got 401 again - re-authenticating didn't help (bad
        // credentials, or the account was disabled/permission-changed
        // server-side). Do NOT loop again - "Do not create an aggressive
        // infinite loop" - surface as terminal so this outbox item goes
        // to FAILED for ops review instead of burning attempts forever.
        this.logger.error("Sync request failed authentication twice in a row - treating as terminal", {
          localIdempotencyKey: payload.localIdempotencyKey,
        });
        return { outcome: "terminal-error", message: "Cloud rejected credentials (401) even after re-authenticating" };
      }

      this.logger.warn("Sync request got 401 - invalidating cached token and retrying once", {
        localIdempotencyKey: payload.localIdempotencyKey,
      });
      this.tokenCache.invalidate();
      // The SAME payload object is reused for the retry - critically,
      // this does NOT go back through ReceptionCaptureWorkflow, so
      // localIdempotencyKey is never regenerated by an auth retry.
      return this.attempt(payload, /* isAuthRetry */ true);
    }

    if (classified.kind === "success") {
      this.logger.info("Sync request succeeded", {
        localIdempotencyKey: payload.localIdempotencyKey,
        outcome: classified.outcome,
        cloudTransactionId: classified.cloudTransactionId,
      });
      return { outcome: classified.outcome, cloudTransactionId: classified.cloudTransactionId };
    }

    if (classified.kind === "retryable") {
      this.logger.warn("Sync request failed - retry scheduled", {
        localIdempotencyKey: payload.localIdempotencyKey,
        status: response.status,
        message: classified.message,
      });
      return { outcome: "retryable-error", message: classified.message };
    }

    this.logger.error("Sync request failed - terminal", {
      localIdempotencyKey: payload.localIdempotencyKey,
      status: response.status,
      message: classified.message,
    });
    return { outcome: "terminal-error", message: classified.message };
  }

  private async fetchWithTimeout(url: string, init: RequestInit): Promise<Response> {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), this.options.requestTimeoutMs);
    try {
      return await this.fetchFn(url, { ...init, signal: controller.signal });
    } finally {
      clearTimeout(timer);
    }
  }
}

/**
 * Deliberately duck-typed (checks `.name`/`.message`, not `err instanceof
 * Error`). Under Jest's "node" test environment specifically, Node's
 * built-in fetch constructs its abort error as a DOMException from the
 * OUTER realm, while test code runs inside a separate vm context with its
 * own `Error` global - so `err instanceof Error` is FALSE there even for
 * a completely genuine timeout abort, even though `err.name`/`err.message`
 * are both present and correct. Found via
 * test/cloud-integration/network-failure.spec.ts's real (not mocked)
 * timeout scenario, which reproduced this exact cross-realm mismatch and
 * got "Unknown network error" for what was actually a real, successful
 * AbortController timeout. Duck-typing sidesteps the realm mismatch
 * entirely and behaves identically under plain Node and under Jest.
 */
function describeNetworkError(err: unknown): string {
  if (err && typeof err === "object") {
    const name = (err as { name?: unknown }).name;
    if (name === "AbortError") return "Request timed out";
    const message = (err as { message?: unknown }).message;
    if (typeof message === "string" && message.length > 0) return message;
  }
  return "Unknown network error";
}
