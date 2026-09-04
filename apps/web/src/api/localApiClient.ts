/**
 * Client for the Gateway's local (edge) read-only HTTP API - see
 * apps/gateway/src/local-api/local-api-server.ts and
 * docs/gateway-architecture.md's "Local vs. cloud responsibility" section.
 *
 * Deliberately a SEPARATE client from api/client.ts's apiFetch(): this
 * talks directly to the gateway process on the operator's own machine/LAN
 * (typically http://localhost:4100), not through the Vite dev proxy or the
 * cloud API's /api prefix, and it authenticates with a different bearer
 * token (localApi.accessToken) that has nothing to do with the cloud JWT.
 * Conflating the two clients would risk accidentally sending the cloud JWT
 * to the gateway or the local token to the cloud - keeping them separate
 * makes that mistake structurally impossible.
 *
 * The local API's URL and token are per-installation values (every centre
 * machine can pick its own port/token) with no natural "build-time config"
 * home in a static frontend bundle, so - following the exact precedent
 * api/client.ts already set for the cloud JWT (localStorage["cc-mc.accessToken"])
 * - they are entered once by the operator and stored in this browser's
 * localStorage, not hardcoded or injected at build time.
 */

const LOCAL_API_URL_KEY = "cc-mc.localApiUrl";
const LOCAL_API_TOKEN_KEY = "cc-mc.localApiToken";

const DEFAULT_LOCAL_API_URL = "http://localhost:4100";

export function getLocalApiUrl(): string {
  return localStorage.getItem(LOCAL_API_URL_KEY) ?? DEFAULT_LOCAL_API_URL;
}

export function setLocalApiUrl(url: string): void {
  localStorage.setItem(LOCAL_API_URL_KEY, url);
}

export function getLocalApiToken(): string | null {
  return localStorage.getItem(LOCAL_API_TOKEN_KEY);
}

export function setLocalApiToken(token: string | null): void {
  if (token) {
    localStorage.setItem(LOCAL_API_TOKEN_KEY, token);
  } else {
    localStorage.removeItem(LOCAL_API_TOKEN_KEY);
  }
}

export class LocalApiError extends Error {
  constructor(
    public status: number | null,
    message: string,
  ) {
    super(message);
  }
}

/**
 * Thin fetch wrapper for the local gateway API, mirroring api/client.ts's
 * apiFetch() shape (so callers/pages feel consistent) but pointed at the
 * gateway's own base URL and its own bearer token. A network failure here
 * (gateway unreachable/offline) is a real, expected state for this
 * specific client - callers should catch LocalApiError / TypeError and
 * render an offline/disconnected state, not treat it as unusual.
 */
export async function localApiFetch<T>(path: string): Promise<T> {
  const token = getLocalApiToken();
  if (!token) {
    throw new LocalApiError(null, "No local API access token configured for this browser.");
  }

  let res: Response;
  try {
    res = await fetch(`${getLocalApiUrl()}${path}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
  } catch (err) {
    throw new LocalApiError(null, `Could not reach the local gateway API: ${(err as Error).message}`);
  }

  if (!res.ok) {
    let message = res.statusText;
    try {
      const body = await res.json();
      message = body.error ?? message;
    } catch {
      // response had no JSON body - fall back to statusText
    }
    throw new LocalApiError(res.status, message);
  }

  return (await res.json()) as T;
}
