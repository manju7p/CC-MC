import { HttpCloudClient, DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS } from "../../src/sync/http-cloud-client";
import { Logger } from "../../src/logging/logger";
import type { ReceptionSyncPayload } from "../../src/sync/cloud-client.types";

function silentLogger(): Logger {
  const logger = new Logger("test");
  jest.spyOn(process.stdout, "write").mockImplementation(() => true);
  return logger;
}

function fakeJwt(expSecondsFromNow = 3600): string {
  const header = Buffer.from(JSON.stringify({ alg: "none", typ: "JWT" })).toString("base64url");
  const exp = Math.floor(Date.now() / 1000) + expSecondsFromNow;
  const payload = Buffer.from(JSON.stringify({ sub: 1, email: "gw@ccmc.local", exp })).toString("base64url");
  return `${header}.${payload}.sig`;
}

const PAYLOAD: ReceptionSyncPayload = {
  localIdempotencyKey: "gw-001:test-key-abc",
  centreId: 1,
  sourceId: 1,
  vehicleId: 1,
  quantityKg: 500,
  fat: 4.2,
  snf: 8.5,
  temperature: 6.0,
};

/** Scripted fetch stub: each call to a URL pops the next queued response for that URL (matched by suffix). Real Response objects for fidelity. */
function makeFetchStub(script: Record<string, Array<{ status: number; body: unknown }>>) {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  const fetchFn = jest.fn(async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    const key = Object.keys(script).find((suffix) => url.endsWith(suffix));
    if (!key) throw new Error(`No scripted response for URL ${url}`);
    const next = script[key].shift();
    if (!next) throw new Error(`Scripted responses for ${key} exhausted`);
    return new Response(JSON.stringify(next.body), { status: next.status });
  }) as unknown as typeof fetch;
  return { fetchFn, calls };
}

function neverRespondingFetch(): typeof fetch {
  return (async (_url: string, init?: RequestInit) => {
    return new Promise<Response>((_resolve, reject) => {
      const signal = init?.signal as AbortSignal | undefined;
      signal?.addEventListener("abort", () => {
        const err = new Error("The operation was aborted");
        err.name = "AbortError";
        reject(err);
      });
    });
  }) as unknown as typeof fetch;
}

function alwaysRejectingFetch(message: string): typeof fetch {
  return (async () => {
    throw new Error(message);
  }) as unknown as typeof fetch;
}

describe("HttpCloudClient", () => {
  it("successful creation: logs in once, POSTs the reception, and returns outcome 'created' with the cloud id", async () => {
    const { fetchFn, calls } = makeFetchStub({
      "/auth/login": [{ status: 201, body: { accessToken: fakeJwt() } }],
      "/reception": [{ status: 201, body: { id: 42, transactionNumber: "BLR-CC-01-42" } }],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result).toEqual({ outcome: "created", cloudTransactionId: 42 });
    const loginCalls = calls.filter((c) => c.url.endsWith("/auth/login"));
    expect(loginCalls).toHaveLength(1);
    const receptionCall = calls.find((c) => c.url.endsWith("/reception"))!;
    expect((receptionCall.init.headers as Record<string, string>).authorization).toMatch(/^Bearer /);
    expect(JSON.parse(receptionCall.init.body as string)).toMatchObject({ localIdempotencyKey: PAYLOAD.localIdempotencyKey });
  });

  it("idempotent duplicate (HTTP 200) classifies as outcome 'duplicate'", async () => {
    const { fetchFn } = makeFetchStub({
      "/auth/login": [{ status: 201, body: { accessToken: fakeJwt() } }],
      "/reception": [{ status: 200, body: { id: 42, transactionNumber: "BLR-CC-01-42" } }],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result).toEqual({ outcome: "duplicate", cloudTransactionId: 42 });
  });

  it("a payload conflict (HTTP 409) classifies as terminal-error, not retryable", async () => {
    const { fetchFn } = makeFetchStub({
      "/auth/login": [{ status: 201, body: { accessToken: fakeJwt() } }],
      "/reception": [{ status: 409, body: { message: "conflict", conflictingFields: ["quantityKg"] } }],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result.outcome).toBe("terminal-error");
    if (result.outcome === "terminal-error") expect(result.message).toBe("conflict");
  });

  it("HTTP 500 classifies as retryable-error", async () => {
    const { fetchFn } = makeFetchStub({
      "/auth/login": [{ status: 201, body: { accessToken: fakeJwt() } }],
      "/reception": [{ status: 500, body: { message: "internal error" } }],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result.outcome).toBe("retryable-error");
  });

  it("a network-level failure (connection refused/dropped) classifies as retryable-error", async () => {
    const { fetchFn: loginFetch } = makeFetchStub({ "/auth/login": [{ status: 201, body: { accessToken: fakeJwt() } }] });
    // First call (login) succeeds via the stub; force the SECOND call
    // (reception) to hit a rejecting fetch by combining both behaviors.
    const combined = jest.fn(async (url: string, init: RequestInit) => {
      if (url.endsWith("/auth/login")) return loginFetch(url, init);
      throw new Error("connect ECONNREFUSED 127.0.0.1:1");
    }) as unknown as typeof fetch;
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, combined);

    const result = await client.sendReception(PAYLOAD);

    expect(result.outcome).toBe("retryable-error");
    if (result.outcome === "retryable-error") expect(result.message).toContain("ECONNREFUSED");
  });

  it("a connection that never responds is aborted after the configured timeout and classifies as retryable-error", async () => {
    const client = new HttpCloudClient(
      "https://cloud.invalid",
      "gw@ccmc.local",
      "pw",
      silentLogger(),
      { requestTimeoutMs: 30 },
      undefined as unknown as typeof fetch,
      // Supply a pre-built token cache so this test isolates the
      // reception request's own timeout behavior from login.
      { getToken: async () => fakeJwt(), invalidate: () => {}, refresh: async () => fakeJwt() } as any,
    );
    // Reaching into the private fetchFn field is the simplest way to swap
    // in the never-responding stub post-construction, without adding a
    // constructor parameter used only by this one test.
    (client as unknown as { fetchFn: typeof fetch }).fetchFn = neverRespondingFetch();

    const start = Date.now();
    const result = await client.sendReception(PAYLOAD);
    const elapsed = Date.now() - start;

    expect(result.outcome).toBe("retryable-error");
    if (result.outcome === "retryable-error") expect(result.message).toMatch(/timed out/i);
    expect(elapsed).toBeLessThan(500); // well under jest's default timeout, proving the abort actually fired
  });

  it("on HTTP 401, invalidates the cached token, re-authenticates, and retries the SAME request once - succeeding on retry", async () => {
    const { fetchFn, calls } = makeFetchStub({
      "/auth/login": [
        { status: 201, body: { accessToken: fakeJwt() } }, // initial login
        { status: 201, body: { accessToken: fakeJwt() } }, // re-login after 401
      ],
      "/reception": [
        { status: 401, body: { message: "Unauthorized" } }, // first attempt: stale/expired token
        { status: 201, body: { id: 99, transactionNumber: "BLR-CC-01-99" } }, // retry after re-auth: succeeds
      ],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result).toEqual({ outcome: "created", cloudTransactionId: 99 });
    expect(calls.filter((c) => c.url.endsWith("/auth/login"))).toHaveLength(2);
    const receptionCalls = calls.filter((c) => c.url.endsWith("/reception"));
    expect(receptionCalls).toHaveLength(2);
    // The idempotency key must be byte-for-byte identical across the
    // original attempt and the post-auth-retry attempt - the retry must
    // NEVER regenerate it.
    const keys = receptionCalls.map((c) => JSON.parse(c.init.body as string).localIdempotencyKey);
    expect(keys[0]).toBe(PAYLOAD.localIdempotencyKey);
    expect(keys[1]).toBe(PAYLOAD.localIdempotencyKey);
  });

  it("two 401s in a row (even after re-authenticating) is terminal, not an infinite retry loop", async () => {
    const { fetchFn, calls } = makeFetchStub({
      "/auth/login": [
        { status: 201, body: { accessToken: fakeJwt() } },
        { status: 201, body: { accessToken: fakeJwt() } },
      ],
      "/reception": [
        { status: 401, body: { message: "Unauthorized" } },
        { status: 401, body: { message: "Unauthorized" } },
      ],
    });
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", "pw", silentLogger(), undefined, fetchFn);

    const result = await client.sendReception(PAYLOAD);

    expect(result.outcome).toBe("terminal-error");
    // Exactly two reception attempts (original + one retry) - not three,
    // not an unbounded loop.
    expect(calls.filter((c) => c.url.endsWith("/reception"))).toHaveLength(2);
  });

  it("never logs the password, the Authorization header value, or the JWT itself, even across a 401 retry", async () => {
    const secretPassword = "correct horse battery staple";
    const token1 = fakeJwt();
    const token2 = fakeJwt();
    const { fetchFn } = makeFetchStub({
      "/auth/login": [
        { status: 201, body: { accessToken: token1 } },
        { status: 201, body: { accessToken: token2 } },
      ],
      "/reception": [
        { status: 401, body: { message: "Unauthorized" } },
        { status: 201, body: { id: 1, transactionNumber: "X-1" } },
      ],
    });
    const logger = new Logger("test");
    const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
    const client = new HttpCloudClient("https://cloud.invalid", "gw@ccmc.local", secretPassword, logger, undefined, fetchFn);

    await client.sendReception(PAYLOAD);

    const loggedText = writeSpy.mock.calls.map((call) => String(call[0])).join("\n");
    expect(loggedText).not.toContain(secretPassword);
    expect(loggedText).not.toContain(token1);
    expect(loggedText).not.toContain(token2);
    expect(loggedText).not.toContain("Bearer ");
  });

  it("DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS has a sane, documented default timeout", () => {
    expect(DEFAULT_HTTP_CLOUD_CLIENT_OPTIONS.requestTimeoutMs).toBeGreaterThan(0);
  });

  // Regression coverage for a real bug caught by
  // test/cloud-integration/network-failure.spec.ts's real (unmocked)
  // timeout scenario: Node's built-in fetch, under Jest's "node" test
  // environment, throws an abort error that is name="AbortError" and has
  // a real .message, but is NOT `instanceof` the test realm's own Error
  // class (a cross-realm/vm-context mismatch) - so a naive
  // `err instanceof Error` check silently falls through to "Unknown
  // network error" for a completely genuine, successful timeout. This
  // fetchFn reproduces that shape directly (an error-like object built
  // WITHOUT `new Error(...)`, so `instanceof Error` is false) without
  // needing a real cross-realm setup.
  it("classifies a non-`instanceof Error` but AbortError-shaped rejection as a timeout, not 'Unknown network error'", async () => {
    const crossRealmAbortError = { name: "AbortError", message: "This operation was aborted" };
    const fetchFn = (async () => {
      throw crossRealmAbortError;
    }) as unknown as typeof fetch;
    const client = new HttpCloudClient(
      "https://cloud.invalid",
      "gw@ccmc.local",
      "pw",
      silentLogger(),
      undefined,
      undefined as unknown as typeof fetch,
      { getToken: async () => fakeJwt(), invalidate: () => {}, refresh: async () => fakeJwt() } as any,
    );
    (client as unknown as { fetchFn: typeof fetch }).fetchFn = fetchFn;

    const result = await client.sendReception(PAYLOAD);

    expect(result.outcome).toBe("retryable-error");
    if (result.outcome === "retryable-error") {
      expect(result.message).toBe("Request timed out");
      expect(result.message).not.toMatch(/unknown network error/i);
    }
  });
});
