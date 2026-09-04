import { CloudAuthTokenCache, decodeJwtExpiryMs } from "../../src/sync/cloud-auth";
import { Logger } from "../../src/logging/logger";

function silentLogger(): Logger {
  const logger = new Logger("test");
  jest.spyOn(process.stdout, "write").mockImplementation(() => true);
  return logger;
}

/** Builds a syntactically-real (unsigned) JWT with the given exp claim, in seconds since epoch. */
function fakeJwt(expSeconds: number): string {
  const header = Buffer.from(JSON.stringify({ alg: "none", typ: "JWT" })).toString("base64url");
  const payload = Buffer.from(JSON.stringify({ sub: 1, email: "gateway@ccmc.local", exp: expSeconds })).toString(
    "base64url",
  );
  return `${header}.${payload}.fake-signature`;
}

describe("decodeJwtExpiryMs", () => {
  it("decodes a well-formed JWT's exp claim to milliseconds", () => {
    const token = fakeJwt(1_700_000_000);
    expect(decodeJwtExpiryMs(token)).toBe(1_700_000_000 * 1000);
  });

  it("returns null for a token with fewer than 3 segments", () => {
    expect(decodeJwtExpiryMs("not-a-jwt")).toBeNull();
    expect(decodeJwtExpiryMs("only.two")).toBeNull();
  });

  it("returns null for a token whose payload segment isn't valid base64url JSON", () => {
    expect(decodeJwtExpiryMs("header.not-valid-json!!!.sig")).toBeNull();
  });

  it("returns null when the payload has no numeric exp claim", () => {
    const payload = Buffer.from(JSON.stringify({ sub: 1 })).toString("base64url");
    expect(decodeJwtExpiryMs(`header.${payload}.sig`)).toBeNull();
  });
});

describe("CloudAuthTokenCache", () => {
  it("calls login() on the first getToken() and caches the result", async () => {
    let now = 1_000_000;
    const login = jest.fn().mockResolvedValue({ accessToken: fakeJwt((now + 60_000) / 1000) });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    const token = await cache.getToken();

    expect(login).toHaveBeenCalledTimes(1);
    expect(token).toEqual(expect.any(String));
  });

  it("returns the cached token without calling login() again while still valid", async () => {
    let now = 1_000_000;
    const login = jest.fn().mockResolvedValue({ accessToken: fakeJwt((now + 60_000) / 1000) });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    const first = await cache.getToken();
    now += 1_000; // still well within the 60s expiry and the 30s refresh margin
    const second = await cache.getToken();

    expect(login).toHaveBeenCalledTimes(1);
    expect(second).toBe(first);
  });

  it("refreshes automatically once the token is within the refresh margin of expiring", async () => {
    let now = 1_000_000;
    let call = 0;
    const login = jest.fn().mockImplementation(async () => {
      call += 1;
      return { accessToken: fakeJwt((now + 60_000) / 1000) };
    });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now, { refreshMarginMs: 30_000 });

    await cache.getToken();
    // Advance past (expiry - margin): expiry is now+60s, margin is 30s, so
    // anything >= now+30s should trigger a refresh.
    now += 31_000;
    await cache.getToken();

    expect(login).toHaveBeenCalledTimes(2);
    expect(call).toBe(2);
  });

  it("invalidate() forces the next getToken() to call login() again even though the cached token hasn't expired", async () => {
    let now = 1_000_000;
    const login = jest.fn().mockResolvedValue({ accessToken: fakeJwt((now + 60_000) / 1000) });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    await cache.getToken();
    cache.invalidate();
    await cache.getToken();

    expect(login).toHaveBeenCalledTimes(2);
  });

  it("refresh() always calls login(), bypassing the cache entirely", async () => {
    let now = 1_000_000;
    const login = jest.fn().mockResolvedValue({ accessToken: fakeJwt((now + 60_000) / 1000) });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    await cache.getToken();
    await cache.refresh();

    expect(login).toHaveBeenCalledTimes(2);
  });

  it("single-flight: concurrent getToken() calls while a login is in-flight share the same request", async () => {
    let now = 1_000_000;
    let resolveLogin!: (v: { accessToken: string }) => void;
    const login = jest.fn().mockImplementation(
      () =>
        new Promise<{ accessToken: string }>((resolve) => {
          resolveLogin = resolve;
        }),
    );
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    const p1 = cache.getToken();
    const p2 = cache.getToken();
    resolveLogin({ accessToken: fakeJwt((now + 60_000) / 1000) });
    const [t1, t2] = await Promise.all([p1, p2]);

    expect(login).toHaveBeenCalledTimes(1);
    expect(t1).toBe(t2);
  });

  it("falls back to a conservative short-lived cache when the token has no decodable exp claim", async () => {
    let now = 1_000_000;
    const login = jest.fn().mockResolvedValue({ accessToken: "not-a-real-jwt" });
    const cache = new CloudAuthTokenCache(login, silentLogger(), () => now);

    await cache.getToken();
    now += 4 * 60_000; // well within the 5-minute fallback
    await cache.getToken();
    expect(login).toHaveBeenCalledTimes(1);

    now += 2 * 60_000; // past the 5-minute fallback
    await cache.getToken();
    expect(login).toHaveBeenCalledTimes(2);
  });

  it("never logs the password, the email/login inputs, or the token value itself", async () => {
    let now = 1_000_000;
    const secretToken = fakeJwt((now + 60_000) / 1000);
    const login = jest.fn().mockResolvedValue({ accessToken: secretToken });
    const logger = new Logger("test");
    const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
    const cache = new CloudAuthTokenCache(login, logger, () => now);

    await cache.getToken();

    const loggedText = writeSpy.mock.calls.map((call) => String(call[0])).join("\n");
    expect(loggedText).not.toContain(secretToken);
    expect(loggedText).not.toContain("password");
  });
});
