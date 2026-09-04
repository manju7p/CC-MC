import { CLOUD_TEST_BASE_URL, GATEWAY_BLR_EMAIL, GATEWAY_BLR_PASSWORD } from "./support/live-cloud-fixture";

describe("cloud-integration fixture smoke test", () => {
  it("the live API is reachable and the seeded gateway account can log in", async () => {
    const res = await fetch(`${CLOUD_TEST_BASE_URL}/auth/login`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ email: GATEWAY_BLR_EMAIL, password: GATEWAY_BLR_PASSWORD }),
    });
    expect(res.status).toBe(201);
    const body = (await res.json()) as { accessToken: string };
    expect(typeof body.accessToken).toBe("string");
  });
});
