import "reflect-metadata";
import { INestApplication, ValidationPipe } from "@nestjs/common";
import { Test } from "@nestjs/testing";
import request from "supertest";
import { AppModule } from "../src/app.module";

/**
 * End-to-end tests for the MVP vertical slice, run against the real
 * PostgreSQL test database (ccmc_test) seeded by test/global-setup.ts -
 * not mocks. This is deliberate: the highest-risk logic here (RBAC +
 * centre scoping + quality validation + audit) only means something when
 * exercised through the real guard/DB stack, per the project's testing
 * priorities (backend integration tests over cosmetic ones).
 *
 * Seeded fixture IDs (from src/seed.ts, in insertion order against a
 * freshly-synchronized schema - see global-setup.ts):
 *   Centres:  1 = Bangalore (BLR-CC-01), 2 = Mysore (MYS-CC-01)
 *   Users:    admin@ccmc.local (Admin, all centres)
 *             manager1@ccmc.local (Manager, centre 1)
 *             operator1@ccmc.local (Operator, centre 1)
 *             operator2@ccmc.local (Operator, centre 2)
 *   Sources:  1 = SRC-BLR-001 (centre 1), 2 = SRC-MYS-001 (centre 2)
 *   Vehicles: 1 = KA01AB1234 (centre 1), 2 = KA09CD5678 (centre 2)
 *   Quality rules (global): FAT 3.0-6.0, SNF 8.0-10.0, TEMPERATURE 0-10
 */
describe("CC-MC MVP vertical slice (e2e)", () => {
  let app: INestApplication;
  let operator1Token: string;
  let operator2Token: string;
  let manager1Token: string;

  beforeAll(async () => {
    const moduleRef = await Test.createTestingModule({ imports: [AppModule] }).compile();
    app = moduleRef.createNestApplication();
    app.useGlobalPipes(new ValidationPipe({ whitelist: true, forbidNonWhitelisted: true, transform: true }));
    await app.init();

    const login = async (email: string, password: string) => {
      const res = await request(app.getHttpServer()).post("/auth/login").send({ email, password });
      if (res.status !== 201 && res.status !== 200) {
        throw new Error(`Login failed for ${email}: ${res.status} ${JSON.stringify(res.body)}`);
      }
      return res.body.accessToken as string;
    };

    operator1Token = await login("operator1@ccmc.local", "Operator@12345");
    operator2Token = await login("operator2@ccmc.local", "Operator@12345");
    manager1Token = await login("manager1@ccmc.local", "Manager@12345");
  });

  afterAll(async () => {
    await app.close();
  });

  // 1. Login succeeds
  it("logs in successfully with correct credentials", async () => {
    const res = await request(app.getHttpServer())
      .post("/auth/login")
      .send({ email: "operator1@ccmc.local", password: "Operator@12345" });

    expect(res.status).toBe(201);
    expect(res.body.accessToken).toEqual(expect.any(String));
    expect(res.body.user.email).toBe("operator1@ccmc.local");
    expect(res.body.user.roles).toContain("Operator");
    expect(res.body.user.centreAccess).toEqual({ allCentres: false, centreIds: [1] });
  });

  // 2. Invalid login fails
  it("rejects login with an incorrect password", async () => {
    const res = await request(app.getHttpServer())
      .post("/auth/login")
      .send({ email: "operator1@ccmc.local", password: "wrong-password" });

    expect(res.status).toBe(401);
    expect(res.body.accessToken).toBeUndefined();
  });

  // 3. Operator can create reception
  it("lets an Operator create a milk reception transaction", async () => {
    const res = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 2450, fat: 4.2, snf: 8.5, temperature: 7.2 });

    expect(res.status).toBe(201);
    expect(res.body.status).toBe("ACCEPTED");
    expect(res.body.transactionNumber).toMatch(/^BLR-CC-01-\d+$/);
  });

  // 4. Unauthorized (unauthenticated) request cannot create reception
  it("rejects reception creation with no auth token", async () => {
    const res = await request(app.getHttpServer())
      .post("/reception")
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 2450, fat: 4.2, snf: 8.5, temperature: 7.2 });

    expect(res.status).toBe(401);
  });

  // 5. User cannot access another centre's data
  it("denies an operator access to another centre's data, for both read and write", async () => {
    const created = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 1000, fat: 4.2, snf: 8.5, temperature: 7.2 });
    expect(created.status).toBe(201);
    const bangaloreTransactionId = created.body.id;

    const getAttempt = await request(app.getHttpServer())
      .get(`/reception/${bangaloreTransactionId}`)
      .set("Authorization", `Bearer ${operator2Token}`);
    expect(getAttempt.status).toBe(403);

    const createAttempt = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator2Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 500, fat: 4.2, snf: 8.5, temperature: 7.2 });
    expect(createAttempt.status).toBe(403);
  });

  // 6. Quality values produce correct status
  it("accepts in-range readings and holds out-of-range readings", async () => {
    const inRange = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 1200, fat: 4.0, snf: 8.5, temperature: 6.0 });
    expect(inRange.status).toBe(201);
    expect(inRange.body.status).toBe("ACCEPTED");

    const outOfRange = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 1200, fat: 1.0, snf: 8.5, temperature: 6.0 });
    expect(outOfRange.status).toBe(201);
    expect(outOfRange.body.status).toBe("HOLD");
    expect(outOfRange.body.reason).toMatch(/FAT/);
  });

  // 7. Manager can override HOLD
  it("lets a Manager override a HOLD transaction to ACCEPTED", async () => {
    const held = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 900, fat: 1.0, snf: 8.5, temperature: 6.0 });
    expect(held.body.status).toBe("HOLD");

    const overridden = await request(app.getHttpServer())
      .post(`/reception/${held.body.id}/override`)
      .set("Authorization", `Bearer ${manager1Token}`)
      .send({ newStatus: "ACCEPTED", reason: "Manually verified acceptable" });

    expect(overridden.status).toBe(201);
    expect(overridden.body.status).toBe("ACCEPTED");
  });

  // 8. Unauthorized user cannot override HOLD
  it("denies an Operator (no RECEPTION_OVERRIDE permission) from overriding a HOLD", async () => {
    const held = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 900, fat: 1.0, snf: 8.5, temperature: 6.0 });
    expect(held.body.status).toBe("HOLD");

    const attempt = await request(app.getHttpServer())
      .post(`/reception/${held.body.id}/override`)
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ newStatus: "ACCEPTED", reason: "Operator trying to self-approve" });

    expect(attempt.status).toBe(403);
  });

  // 9. Audit record is created
  it("creates an audit record for a reception creation", async () => {
    const created = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 750, fat: 4.2, snf: 8.5, temperature: 7.2 });

    const auditRes = await request(app.getHttpServer())
      .get("/audit-logs")
      .query({ resourceType: "MilkReceptionTransaction" })
      .set("Authorization", `Bearer ${manager1Token}`);

    expect(auditRes.status).toBe(200);
    const match = auditRes.body.find(
      (entry: any) => entry.action === "RECEPTION_CREATE" && entry.resourceId === String(created.body.id),
    );
    expect(match).toBeDefined();
    expect(match.userId).toBeDefined();
    expect(match.centreId).toBe(1);
  });

  // 10. Dashboard respects centre scope
  it("scopes the dashboard summary to the caller's centre access", async () => {
    await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 300, fat: 4.2, snf: 8.5, temperature: 7.2 });

    const blrSummary = await request(app.getHttpServer())
      .get("/dashboard/summary")
      .set("Authorization", `Bearer ${operator1Token}`);
    expect(blrSummary.status).toBe(200);
    expect(blrSummary.body.centreIds).toEqual([1]);
    expect(blrSummary.body.totalTransactions).toBeGreaterThan(0);

    const mysSummary = await request(app.getHttpServer())
      .get("/dashboard/summary")
      .set("Authorization", `Bearer ${operator2Token}`);
    expect(mysSummary.status).toBe(200);
    expect(mysSummary.body.centreIds).toEqual([2]);
    // Mysore operator has created no transactions in this suite, so its
    // count must be unaffected by everything Bangalore did above - this is
    // the actual centre-isolation assertion, not just "the field exists".
    expect(mysSummary.body.totalTransactions).toBe(0);
  });

  // --- Added during the Principal Engineer hardening review -------------
  // Scenarios explicitly called out by that review that the original 10
  // scenarios didn't exercise: malformed JWTs, cross-centre IDOR on
  // update (not just read/create), illegal status transitions, and direct
  // status injection on create.

  it("rejects a request with a malformed/tampered JWT", async () => {
    const malformed = await request(app.getHttpServer())
      .get("/reception")
      .set("Authorization", "Bearer not-a-real-jwt");
    expect(malformed.status).toBe(401);

    // A syntactically valid JWT whose signature doesn't match any secret
    // this server would issue - simulates a forged/tampered token.
    const forgedButWellFormed =
      "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOjEsImVtYWlsIjoiYWRtaW5AY2NtYy5sb2NhbCJ9.wrongsignaturewrongsignaturewrongsignature";
    const forged = await request(app.getHttpServer())
      .get("/reception")
      .set("Authorization", `Bearer ${forgedButWellFormed}`);
    expect(forged.status).toBe(401);
  });

  it("denies an operator from updating another centre's source or vehicle (IDOR on update)", async () => {
    // Source 1 / Vehicle 1 belong to Bangalore (centre 1); operator2 is
    // scoped to Mysore (centre 2) only.
    const sourceUpdate = await request(app.getHttpServer())
      .patch("/sources/1")
      .set("Authorization", `Bearer ${operator2Token}`)
      .send({ name: "Hijacked name" });
    expect(sourceUpdate.status).toBe(403);

    const vehicleUpdate = await request(app.getHttpServer())
      .patch("/vehicles/1")
      .set("Authorization", `Bearer ${operator2Token}`)
      .send({ driverName: "Hijacked driver" });
    expect(vehicleUpdate.status).toBe(403);
  });

  it("rejects overriding a transaction that is not currently on HOLD", async () => {
    const accepted = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({ centreId: 1, sourceId: 1, vehicleId: 1, quantityKg: 400, fat: 4.2, snf: 8.5, temperature: 7.2 });
    expect(accepted.body.status).toBe("ACCEPTED");

    const attempt = await request(app.getHttpServer())
      .post(`/reception/${accepted.body.id}/override`)
      .set("Authorization", `Bearer ${manager1Token}`)
      .send({ newStatus: "REJECTED", reason: "Trying to override an already-ACCEPTED transaction" });

    expect(attempt.status).toBe(400);
  });

  it("rejects a reception payload that tries to set status directly instead of going through quality validation", async () => {
    const attempt = await request(app.getHttpServer())
      .post("/reception")
      .set("Authorization", `Bearer ${operator1Token}`)
      .send({
        centreId: 1,
        sourceId: 1,
        vehicleId: 1,
        quantityKg: 400,
        fat: 4.2,
        snf: 8.5,
        temperature: 7.2,
        status: "ACCEPTED", // not a field on CreateReceptionDto
      });

    // whitelist + forbidNonWhitelisted on the global ValidationPipe means an
    // unrecognized field is a 400, not a silently-ignored or silently-applied one.
    expect(attempt.status).toBe(400);
  });

  it("rejects a non-numeric centreId filter on dashboard and audit-log queries instead of silently misinterpreting it", async () => {
    const dashboard = await request(app.getHttpServer())
      .get("/dashboard/summary")
      .query({ centreId: "not-a-number" })
      .set("Authorization", `Bearer ${manager1Token}`);
    expect(dashboard.status).toBe(400);

    const audit = await request(app.getHttpServer())
      .get("/audit-logs")
      .query({ centreId: "not-a-number" })
      .set("Authorization", `Bearer ${manager1Token}`);
    expect(audit.status).toBe(400);
  });
});
