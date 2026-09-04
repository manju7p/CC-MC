import { Logger } from "../logging/logger";

export interface CloudLoginResult {
  accessToken: string;
}

/** Obtains a fresh access token - the ONLY place a login request is actually made. Injected so tests never need a real HTTP call. */
export type CloudLoginFn = () => Promise<CloudLoginResult>;

export interface CloudAuthOptions {
  /**
   * Safety margin subtracted from the token's real expiry, so a request
   * already in flight never races a token that expires mid-request. The
   * cloud API's default JWT lifetime is 8 hours (apps/api/.env's
   * JWT_EXPIRES_IN) - 30s is a small fraction of that, generous enough to
   * cover normal request latency without forcing needlessly frequent
   * re-logins.
   */
  refreshMarginMs: number;
}

export const DEFAULT_CLOUD_AUTH_OPTIONS: CloudAuthOptions = { refreshMarginMs: 30_000 };

/**
 * Caches the gateway's cloud auth token and knows when to refresh it -
 * the "obtain token -> cache token -> use token -> receive 401/expiry ->
 * re-authenticate -> retry safely" strategy the checkpoint asks for,
 * split out as its own small, independently-testable unit (HttpCloudClient
 * owns the actual HTTP retry-on-401 orchestration; this class only owns
 * "is what I'm holding still good, and if not, how do I get a new one").
 *
 * NEVER logs the email, password, or the token itself - every log
 * statement here logs only booleans/timestamps/durations. The token value
 * is held in a private field and is only ever placed into an
 * Authorization header by HttpCloudClient, never written to a log line.
 *
 * Single-flight: concurrent getToken()/refresh() calls while a login is
 * already in progress share the SAME in-flight request rather than firing
 * a second one - relevant if a future change makes SyncEngine process
 * multiple outbox items concurrently (today it processes one at a time
 * per tick - see sync-engine.ts - so this is a defensive property, not
 * something today's call pattern currently exercises under real
 * concurrency, but it costs nothing and removes a footgun for later).
 */
export class CloudAuthTokenCache {
  private cachedToken: string | null = null;
  private expiresAtMs: number | null = null;
  private inFlight: Promise<string> | null = null;

  constructor(
    private readonly login: CloudLoginFn,
    private readonly logger: Logger,
    private readonly now: () => number = () => Date.now(),
    private readonly options: CloudAuthOptions = DEFAULT_CLOUD_AUTH_OPTIONS,
  ) {}

  /** Returns the cached token if it's still valid (with margin), otherwise obtains and caches a fresh one. */
  async getToken(): Promise<string> {
    if (this.cachedToken !== null && this.expiresAtMs !== null) {
      if (this.now() < this.expiresAtMs - this.options.refreshMarginMs) {
        return this.cachedToken;
      }
    }
    return this.refresh();
  }

  /** Forces a fresh token, bypassing the cache. Concurrent callers share one in-flight login(). */
  async refresh(): Promise<string> {
    if (this.inFlight) return this.inFlight;
    this.inFlight = this.doRefresh();
    try {
      return await this.inFlight;
    } finally {
      this.inFlight = null;
    }
  }

  private async doRefresh(): Promise<string> {
    this.logger.info("Requesting a new cloud auth token");
    const result = await this.login();
    const decodedExpiryMs = decodeJwtExpiryMs(result.accessToken);
    // A token this cache can't decode an expiry from (malformed, or a
    // future auth scheme change) is still usable - just cached
    // conservatively for a short window so a stale cache can't live
    // forever unnoticed, and re-validated against the server via the
    // normal 401-triggers-refresh path if it turns out to already be bad.
    this.expiresAtMs = decodedExpiryMs ?? this.now() + 5 * 60_000;
    this.cachedToken = result.accessToken;
    this.logger.info("Cloud auth token obtained", {
      expiresInMs: this.expiresAtMs - this.now(),
      decodedExpiry: decodedExpiryMs !== null,
    });
    return this.cachedToken;
  }

  /** Discards the cached token - called after a 401 so the next getToken() forces a fresh login. */
  invalidate(): void {
    this.cachedToken = null;
    this.expiresAtMs = null;
  }
}

/**
 * Reads the standard `exp` claim (seconds since epoch) out of a JWT's
 * payload segment, WITHOUT verifying the signature - this is only ever
 * used on a token this same gateway just received directly from its own
 * login call over HTTPS, to know when to proactively refresh it, not to
 * make any trust decision (the cloud API's JwtStrategy is the only
 * component that ever verifies a token). Returns null (never throws) for
 * anything that doesn't decode cleanly, so a token whose shape ever
 * changes degrades to the conservative fallback in doRefresh() above
 * instead of crashing the gateway.
 */
export function decodeJwtExpiryMs(token: string): number | null {
  try {
    const parts = token.split(".");
    if (parts.length !== 3) return null;
    const payloadJson = Buffer.from(parts[1], "base64url").toString("utf-8");
    const payload = JSON.parse(payloadJson) as { exp?: unknown };
    if (typeof payload.exp !== "number") return null;
    return payload.exp * 1000;
  } catch {
    return null;
  }
}
