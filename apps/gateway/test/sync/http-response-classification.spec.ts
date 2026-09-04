import { classifyHttpResponse } from "../../src/sync/http-response-classification";

describe("classifyHttpResponse", () => {
  it("201 with a numeric id classifies as success/created", () => {
    expect(classifyHttpResponse(201, { id: 42 })).toEqual({ kind: "success", outcome: "created", cloudTransactionId: 42 });
  });

  it("200 with a numeric id classifies as success/duplicate (idempotent-success)", () => {
    expect(classifyHttpResponse(200, { id: 42 })).toEqual({ kind: "success", outcome: "duplicate", cloudTransactionId: 42 });
  });

  it("a 2xx success with no numeric id in the body is terminal, not silently treated as synced", () => {
    const result = classifyHttpResponse(201, { message: "ok, but no id" });
    expect(result.kind).toBe("terminal");
  });

  // Regression coverage for a real bug caught by
  // test/cloud-integration/concurrent-duplicate.spec.ts: the cloud API
  // (apps/api/src/reception/reception.service.ts) returns HTTP 201 for
  // BOTH a fresh create AND an idempotent-duplicate-return - it does NOT
  // vary the status code, so outcome must be read from the response
  // body's `outcome` field, never inferred from the status code alone.
  it("HTTP 201 with body.outcome=\"duplicate\" classifies as duplicate, NOT created (the real API always returns 201 for both)", () => {
    const result = classifyHttpResponse(201, { id: 42, outcome: "duplicate" });
    expect(result).toEqual({ kind: "success", outcome: "duplicate", cloudTransactionId: 42 });
  });

  it("HTTP 201 with body.outcome=\"created\" classifies as created", () => {
    const result = classifyHttpResponse(201, { id: 42, outcome: "created" });
    expect(result).toEqual({ kind: "success", outcome: "created", cloudTransactionId: 42 });
  });

  it("falls back to the status-code heuristic only when the body has no outcome field at all", () => {
    expect(classifyHttpResponse(201, { id: 1 })).toEqual({ kind: "success", outcome: "created", cloudTransactionId: 1 });
    expect(classifyHttpResponse(200, { id: 1 })).toEqual({ kind: "success", outcome: "duplicate", cloudTransactionId: 1 });
  });

  it("401 classifies as auth-retryable", () => {
    expect(classifyHttpResponse(401, { message: "Unauthorized" })).toEqual({ kind: "auth-retryable" });
  });

  it("409 (idempotency payload conflict) classifies as terminal, not retryable", () => {
    const result = classifyHttpResponse(409, { message: "conflict", conflictingFields: ["quantityKg"] });
    expect(result.kind).toBe("terminal");
    if (result.kind === "terminal") expect(result.message).toBe("conflict");
  });

  it("409 with no message body still classifies as terminal with a sensible fallback message", () => {
    const result = classifyHttpResponse(409, null);
    expect(result.kind).toBe("terminal");
    if (result.kind === "terminal") expect(result.message).toMatch(/conflict/i);
  });

  it.each([429, 500, 502, 503, 504])("HTTP %d classifies as retryable", (status) => {
    const result = classifyHttpResponse(status, { message: "try again" });
    expect(result.kind).toBe("retryable");
  });

  it.each([400, 403, 404, 422])("HTTP %d classifies as terminal (retrying the same payload would never succeed)", (status) => {
    const result = classifyHttpResponse(status, { message: "bad request" });
    expect(result.kind).toBe("terminal");
  });

  it("extracts a string message when present", () => {
    const result = classifyHttpResponse(500, { message: "database unavailable" });
    expect(result.kind).toBe("retryable");
    if (result.kind === "retryable") expect(result.message).toBe("database unavailable");
  });

  it("extracts and joins a class-validator style array message", () => {
    const result = classifyHttpResponse(400, { message: ["centreId must be an integer", "fat must be a number"] });
    expect(result.kind).toBe("terminal");
    if (result.kind === "terminal") expect(result.message).toBe("centreId must be an integer; fat must be a number");
  });

  it("falls back to a generic message when the body has no usable message field", () => {
    const result = classifyHttpResponse(500, null);
    expect(result.kind).toBe("retryable");
    if (result.kind === "retryable") expect(result.message).toContain("500");
  });
});
