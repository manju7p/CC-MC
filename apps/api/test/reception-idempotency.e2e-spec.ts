import "reflect-metadata";
import { INestApplication, ValidationPipe } from "@nestjs/common";
import { Test } from "@nestjs/testing";
import request from "supertest";
import { AppModule } from "../src/app.module";

/**
 * Checkpoint 5 (Local Device Gateway cloud sync) e2e tests: cloud-side
 * idempotency, the concurrent-request races the checkpoint explicitly
 * requires, and the gateway service accounts' centre scoping / least
 * privilege. Runs against the same real PostgreSQL test database and
 * seed data as app.e2e-spec.ts (see that file's header comment for the
 * full fixture list) - this file adds only the two gateway service
 * accounts to what it reads:
 *   gateway-blr-cc-01@ccmc.local (GatewayService, centre 1 / Bangalore only)
 *   gateway-mys-cc-01@ccmc.local (GatewayService, centre 2 / Mysore only)
 *
 * "Exactly one audit event for transaction X" is checked via
 * GET /audit-logs?resourceType=MilkReceptionTransaction, filtered
 * client-side by resourceId - there is no per-resource audit endpoint
 * (Rule 4: not built until something needs it), so this is the same
 * mechanism app.e2e-spec.ts's own audit test already uses.
 */
describe("Checkpoint 5: cloud reception idempotency, concurrency, and gateway centre security (e2e)", () => {
  let app: INestApplication;
  let gatewayBlrToken: string;
  let gatewayMysToken: string;
  let manager1Token: string;

  const login = async (email: string, password: string): Promise<string> => {
    const res = await request(app.getHttpServer()).post("/auth/login").send({ email, password });
    if (res.status !== 201 && res.status !== 200) {
      throw new Error(`Login failed for ${email}: ${res.status} ${JSON.stringify(res.body)}`);
    }
    return res.body.accessToken as string;
  };

  const createReception = (token: string, body: Record<string, unknown>) =>
    request(app.getHttpServer()).post("/reception").set("Authorization", `Bearer ${token}`).send(body);

  const auditCountFor = async (resourceId: number): Promise<number> => {
    const res = await request(app.getHttpServer())
      .get("/audit-logs")
      .query({ resourceType: "MilkReceptionTransaction" })
      .set("Authorization", `Bearer ${manager1Token}`);
    expect(res.status).toBe(200);
    return (res.body as Array<{ action: string; resourceId: string }>).filter(
      (entry) => entry.action === "RECEPTION_CREATE" && entry.resourceId === String(resourceId),
    ).length;
  };

  const uniqueKey = (label: string) => `test-${label}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

  const basePayload = (overrides: Record<string, unknown> = {}) => ({
    centreId: 1,
    sourceId: 1,
    vehicleId: 1,
    quantityKg: 500,
    fat: 4.2,
    snf: 8.5,
    temperature: 6.0,
    ...overrides,
  });

  beforeAll(async () => {
    const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile();
    app = moduleRef.createNestApplication();
    app.useGlobalPipes(new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }));
    await app.init();

    gatewayBlrToken = await login("gateway-blr-cc-01@ccmc.local", "GatewayBLR@2026!sync");
    gatewayMysToken = await login("gateway-mys-cc-01@ccmc.local", "GatewayMYS@2026!sync");
    manager1Token = await login("manager1@ccmc.local", "Manager@12345");
  });

  afterAll(async () => {
    await app.close();
  });

  // --- Gateway centre security -----------------------------------------

  it("lets the Bangalore gateway service account create a reception for its own centre", async () => {
    const res = await createReception(gatewayBlrToken, basePayload({ localIdempotencyKey: uniqueKey("own-centre") }));
    expect(res.status).toBe(201);
    expect(res.body.outcome).toBe("created");
    expect(res.body.centreId).toBe(1);
    expect(res.body.transactionNumber).toMatch(/^BLR-CC-01-\d+$/);
  });

  it("denies the Bangalore gateway service account from creating a reception for Mysore (centre 2)", async () => {
    const res = await createReception(
      gatewayBlrToken,
      basePayload({ centreId: 2, sourceId: 2, vehicleId: 2, localIdempotencyKey: uniqueKey("cross-centre") }),
    );
    expect(res.status).toBe(403);
  });

  it("denies the Mysore gateway service account from creating a reception for Bangalore (centre 1)", async () => {
    const res = await createReception(gatewayMysToken, basePayload({ localIdempotencyKey: uniqueKey("cross-centre-2") }));
    expect(res.status).toBe(403);
  });

  it("a gateway service account holds RECEPTION_CREATE only - it cannot even read reception data (least privilege)", async () => {
    const listAttempt = await request(app.getHttpServer())
      .get("/reception")
      .set("Authorization", `Bearer ${gatewayBlrToken}`);
    expect(listAttempt.status).toBe(403);

    const dashboardAttempt = await request(app.getHttpServer())
      .get("/dashboard/summary")
      .set("Authorization", `Bearer ${gatewayBlrToken}`);
    expect(dashboardAttempt.status).toBe(403);
  });

  // --- Cloud idempotency (sequential) -----------------------------------

  it("a retry with the SAME localIdempotencyKey and the SAME payload returns the existing transaction, creating no new row or audit event", async () => {
    const key = uniqueKey("same-payload-retry");
    const payload = basePayload({ localIdempotencyKey: key });

    const first = await createReception(gatewayBlrToken, payload);
    expect(first.status).toBe(201);
    expect(first.body.outcome).toBe("created");
    const transactionId = first.body.id;
    const transactionNumber = first.body.transactionNumber;

    const second = await createReception(gatewayBlrToken, payload);
    expect(second.status).toBe(201);
    expect(second.body.outcome).toBe("duplicate");
    expect(second.body.id).toBe(transactionId);
    expect(second.body.transactionNumber).toBe(transactionNumber);

    expect(await auditCountFor(transactionId)).toBe(1);
  });

  it("a retry with the SAME localIdempotencyKey but a DIFFERENT payload returns an explicit 409 conflict, leaving the original untouched", async () => {
    const key = uniqueKey("conflicting-payload-retry");
    const original = basePayload({ localIdempotencyKey: key, quantityKg: 500 });

    const first = await createReception(gatewayBlrToken, original);
    expect(first.status).toBe(201);
    const transactionId = first.body.id;

    const conflicting = await createReception(gatewayBlrToken, { ...original, quantityKg: 999 });
    expect(conflicting.status).toBe(409);
    expect(conflicting.body.conflictingFields).toEqual(["quantityKg"]);
    expect(conflicting.body.existingTransactionId).toBe(transactionId);

    // The original row must be completely unmodified by the rejected retry.
    const refetched = await request(app.getHttpServer())
      .get(`/reception/${transactionId}`)
      .set("Authorization", `Bearer ${manager1Token}`);
    expect(Number(refetched.body.quantityKg)).toBe(500);

    expect(await auditCountFor(transactionId)).toBe(1);
  });

  it("an idempotency key is scoped by its exact payload, not just a subset - a conflicting fat/snf combination is also rejected", async () => {
    const key = uniqueKey("multi-field-conflict");
    const original = basePayload({ localIdempotencyKey: key, fat: 4.2, snf: 8.5 });

    const first = await createReception(gatewayBlrToken, original);
    expect(first.status).toBe(201);

    const conflicting = await createReception(gatewayBlrToken, { ...original, fat: 5.0, snf: 9.0 });
    expect(conflicting.status).toBe(409);
    expect(conflicting.body.conflictingFields.sort()).toEqual(["fat", "snf"]);
  });

  it("a request with no localIdempotencyKey behaves exactly as before (existing web workflow, unmodified)", async () => {
    const res = await createReception(gatewayBlrToken, basePayload({ quantityKg: 321 }));
    expect(res.status).toBe(201);
    expect(res.body.outcome).toBe("created"); // additive field; existing web callers simply never read it
    expect(res.body.localIdempotencyKey).toBeNull();
  });

  // --- Concurrent duplicate test (mandatory) -----------------------------

  it("CONCURRENT identical requests (same key, same payload) resolve to exactly ONE reception, ONE transaction number, and ONE audit event", async () => {
    const key = uniqueKey("concurrent-same-payload");
    const payload = basePayload({ localIdempotencyKey: key, quantityKg: 777 });

    const [a, b] = await Promise.all([createReception(gatewayBlrToken, payload), createReception(gatewayBlrToken, payload)]);

    expect([a.status, b.status].sort()).toEqual([201, 201]);
    expect(a.body.id).toBe(b.body.id);
    expect(a.body.transactionNumber).toBe(b.body.transactionNumber);
    // Exactly one of the two actually inserted the row; the other observed
    // it as an idempotent duplicate - never both "created".
    const outcomes = [a.body.outcome, b.body.outcome].sort();
    expect(outcomes).toEqual(["created", "duplicate"]);

    expect(await auditCountFor(a.body.id)).toBe(1);
  });

  it("CONCURRENT conflicting requests (same key, DIFFERENT payload) resolve to exactly ONE transaction; the loser gets an explicit conflict", async () => {
    const key = uniqueKey("concurrent-conflicting-payload");
    const payloadX = basePayload({ localIdempotencyKey: key, quantityKg: 111 });
    const payloadY = basePayload({ localIdempotencyKey: key, quantityKg: 222 });

    const [a, b] = await Promise.all([createReception(gatewayBlrToken, payloadX), createReception(gatewayBlrToken, payloadY)]);

    const statuses = [a.status, b.status].sort((x, y) => x - y);
    // Exactly one request wins (201, either created or duplicate-of-itself
    // is impossible here since payloads differ - it must be "created");
    // the other MUST see a payload conflict (409), never a silent 201.
    expect(statuses).toEqual([201, 409]);

    const winner = a.status === 201 ? a : b;
    const loser = a.status === 409 ? a : b;
    expect(winner.body.outcome).toBe("created");
    expect(loser.body.conflictingFields).toEqual(["quantityKg"]);
    expect(loser.body.existingTransactionId).toBe(winner.body.id);

    expect(await auditCountFor(winner.body.id)).toBe(1);
  });
});
