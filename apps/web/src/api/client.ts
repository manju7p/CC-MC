const TOKEN_STORAGE_KEY = "cc-mc.accessToken";

let currentToken: string | null = localStorage.getItem(TOKEN_STORAGE_KEY);

export function getToken(): string | null {
  return currentToken;
}

export function setToken(token: string | null): void {
  currentToken = token;
  if (token) {
    localStorage.setItem(TOKEN_STORAGE_KEY, token);
  } else {
    localStorage.removeItem(TOKEN_STORAGE_KEY);
  }
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

/**
 * Fires whenever any apiFetch call gets a 401 - lets AuthContext clear its
 * in-memory `user` even when the 401 came from a call OTHER than
 * `/auth/me`/`/auth/login` (e.g. the JWT expired mid-session while the
 * operator was on the Dashboard). Without this, setToken(null) below
 * cleared the stored token, but AuthContext's `user` stayed stale/truthy,
 * so ProtectedRoute kept rendering the page instead of bouncing to
 * /login - a real "expired auth not handled" gap, not a hypothetical one.
 * A plain module-level callback (not a full event-emitter library) is
 * enough for the one listener AuthContext registers.
 */
let onUnauthorized: (() => void) | null = null;
export function setUnauthorizedHandler(fn: (() => void) | null): void {
  onUnauthorized = fn;
}

/**
 * Thin fetch wrapper - deliberately not a generated client or a heavier
 * data-fetching library (Rule 4: no unnecessary abstractions for tonight).
 * Every call attaches the bearer token; a 401 clears the stored token and
 * notifies AuthContext so the app falls back to the login screen.
 */
export async function apiFetch<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = {
    "Content-Type": "application/json",
    ...(options.headers as Record<string, string> | undefined),
  };
  if (currentToken) {
    headers.Authorization = `Bearer ${currentToken}`;
  }

  const res = await fetch(`/api${path}`, { ...options, headers });

  if (res.status === 401) {
    setToken(null);
    onUnauthorized?.();
  }

  if (!res.ok) {
    let message = res.statusText;
    try {
      const body = await res.json();
      message = body.message ?? message;
    } catch {
      // response had no JSON body - fall back to statusText
    }
    throw new ApiError(res.status, Array.isArray(message) ? message.join(", ") : message);
  }

  if (res.status === 204) {
    return undefined as T;
  }
  return (await res.json()) as T;
}
