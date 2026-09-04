import * as crypto from "crypto";
import * as http from "http";
import type { IncomingMessage, ServerResponse } from "http";
import type {
  LocalGatewayStatusDto,
  LocalReceptionSummaryDto,
  LocalTodaySummaryDto,
  LocalTransactionsResponseDto,
} from "@cc-mc/shared-types";
import type { GatewayConfig } from "../config/config.types";
import { Logger } from "../logging/logger";
import type { HealthService } from "../health/health.service";
import type { LocalStorage } from "../storage/storage.types";
import type { LocalTransaction, OutboxRecord } from "../storage/local-transaction.types";
import { istTodayBoundsUtc } from "./local-day-bounds";

/**
 * A minimal, read-only local (edge) HTTP API exposed by the long-running
 * Gateway process, added so the React frontend can show operator-facing
 * local state (today's collection, recent transactions, sync/gateway
 * status) WITHOUT the browser ever touching SQLite directly and without
 * any SQLite access code inside apps/web - see this checkpoint's
 * directive and docs/gateway-architecture.md's "Local vs. cloud
 * responsibility" section for the full reasoning.
 *
 * Deliberately built on Node's built-in `http` module rather than a
 * framework (Express/Fastify/etc.) - three GET routes, one auth check,
 * and CORS headers do not justify a new dependency for this boring,
 * minimal-deps gateway process (Rule 11), the same reasoning that kept
 * node:sqlite over better-sqlite3.
 *
 * Trust domain: gated by a SEPARATE shared bearer token
 * (config.localApi.accessToken), never the cloud's JWT_SECRET and never
 * a cloud-issued JWT. See config.types.ts's localApi doc comment for why
 * conflating these would be a real regression to the cloud signing
 * secret's blast radius. This server:
 *   - never proxies to the cloud API,
 *   - never accepts cloud credentials or forwards them anywhere,
 *   - never exposes raw SQLite query access,
 *   - only ever returns data already durable in gateway.sqlite via the
 *     existing LocalStorage interface (no new schema, no invented
 *     fields - see the DTOs' own doc comments in shared-types for the
 *     ACCEPTED/HOLD gap this deliberately does NOT fabricate),
 *   - binds to 127.0.0.1 by default (config.localApi.bindAddress can
 *     override this only when LAN access is genuinely required).
 */
export class LocalApiServer {
  private server: http.Server | null = null;
  private readonly bindAddress: string;

  constructor(
    private readonly config: GatewayConfig,
    private readonly storage: LocalStorage,
    private readonly health: HealthService,
    private readonly logger: Logger = new Logger("local-api"),
  ) {
    if (!config.localApi) {
      throw new Error("LocalApiServer constructed without config.localApi - caller must check config.localApi?.enabled first.");
    }
    this.bindAddress = config.localApi.bindAddress ?? "127.0.0.1";
  }

  async start(): Promise<void> {
    if (this.server) return;
    const port = this.requireLocalApiConfig().port;

    await new Promise<void>((resolve, reject) => {
      const server = http.createServer((req, res) => {
        this.handleRequest(req, res).catch((err) => {
          this.logger.error("Unhandled error in local API request handler", { error: (err as Error).message });
          if (!res.headersSent) {
            this.writeJson(res, 500, { error: "internal_error" });
          }
        });
      });
      server.on("error", reject);
      server.listen(port, this.bindAddress, () => {
        server.off("error", reject);
        this.server = server;
        this.logger.info("Local API listening", { bindAddress: this.bindAddress, port });
        resolve();
      });
    });
  }

  async stop(): Promise<void> {
    const server = this.server;
    if (!server) return;
    this.server = null;
    await new Promise<void>((resolve, reject) => {
      server.close((err) => (err ? reject(err) : resolve()));
    });
    this.logger.info("Local API stopped");
  }

  private requireLocalApiConfig(): NonNullable<GatewayConfig["localApi"]> {
    if (!this.config.localApi) {
      throw new Error("LocalApiServer: config.localApi is missing.");
    }
    return this.config.localApi;
  }

  private async handleRequest(req: IncomingMessage, res: ServerResponse): Promise<void> {
    this.applyCorsHeaders(res);

    if (req.method === "OPTIONS") {
      // CORS preflight - no auth required, no data returned.
      res.writeHead(204);
      res.end();
      return;
    }

    if (req.method !== "GET") {
      this.writeJson(res, 405, { error: "method_not_allowed" });
      return;
    }

    if (!this.isAuthorized(req)) {
      this.writeJson(res, 401, { error: "unauthorized" });
      return;
    }

    const url = new URL(req.url ?? "/", `http://${this.bindAddress}:${this.requireLocalApiConfig().port}`);

    if (url.pathname === "/local/status") {
      await this.handleStatus(res);
      return;
    }
    if (url.pathname === "/local/today") {
      await this.handleToday(res);
      return;
    }
    if (url.pathname === "/local/transactions") {
      await this.handleTransactions(res, url.searchParams);
      return;
    }

    this.writeJson(res, 404, { error: "not_found" });
  }

  private applyCorsHeaders(res: ServerResponse): void {
    // Access-Control-Allow-Origin: * is deliberate, not an oversight - this
    // API has no cookie-based session to protect (the frontend sends the
    // access token as an explicit Authorization header, which a
    // cross-origin page cannot silently attach on the victim's behalf the
    // way it can with cookies), and it returns only read-only local
    // operational data, never write access or credentials.
    res.setHeader("Access-Control-Allow-Origin", "*");
    res.setHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
    res.setHeader("Access-Control-Allow-Headers", "Authorization, Content-Type");
  }

  private isAuthorized(req: IncomingMessage): boolean {
    const header = req.headers.authorization;
    if (!header || !header.startsWith("Bearer ")) return false;
    const provided = header.slice("Bearer ".length);
    const expected = this.requireLocalApiConfig().accessToken;

    // Constant-time comparison - this token is a bearer secret, same class
    // of value as cloudAuthPassword, so it gets the same "don't leak
    // timing information" treatment even though the realistic attack
    // surface here is localhost/LAN only.
    const providedBuf = Buffer.from(provided);
    const expectedBuf = Buffer.from(expected);
    if (providedBuf.length !== expectedBuf.length) return false;
    return crypto.timingSafeEqual(providedBuf, expectedBuf);
  }

  private writeJson(res: ServerResponse, status: number, body: unknown): void {
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(JSON.stringify(body));
  }

  private async handleStatus(res: ServerResponse): Promise<void> {
    const snapshot = this.health.getSnapshot();
    const dto: LocalGatewayStatusDto = {
      gatewayId: snapshot.gatewayId,
      centreId: snapshot.centreId,
      version: snapshot.version,
      startedAt: snapshot.startedAt,
      uptimeSeconds: snapshot.uptimeSeconds,
      serviceState: snapshot.serviceState,
      cloudConnectivity: snapshot.cloudConnectivity,
      deviceConnectivity: snapshot.deviceConnectivity,
      pendingSyncCount: snapshot.pendingSyncCount,
    };
    this.writeJson(res, 200, dto);
  }

  private async handleToday(res: ServerResponse): Promise<void> {
    const bounds = istTodayBoundsUtc(new Date());
    const transactions = await this.storage.listLocalTransactionsInRange(bounds.start, bounds.end);
    const dtos = await this.toSummaryDtos(transactions);

    const dto: LocalTodaySummaryDto = {
      dateLabel: bounds.dateLabel,
      totalTransactions: dtos.length,
      totalQuantityKg: dtos.reduce((sum, t) => sum + t.quantityKg, 0),
      transactions: dtos,
    };
    this.writeJson(res, 200, dto);
  }

  private async handleTransactions(res: ServerResponse, searchParams: URLSearchParams): Promise<void> {
    const rawLimit = Number(searchParams.get("limit") ?? "50");
    // Same defend-against-a-hostile-query-param clamp anywhere else in this
    // codebase accepts a caller-supplied limit: an invalid or absurd value
    // falls back to a sane default rather than erroring or querying
    // unbounded rows.
    const limit = Number.isInteger(rawLimit) && rawLimit > 0 && rawLimit <= 200 ? rawLimit : 50;

    const transactions = await this.storage.listRecentLocalTransactions(limit);
    const dtos = await this.toSummaryDtos(transactions);
    const dto: LocalTransactionsResponseDto = { transactions: dtos };
    this.writeJson(res, 200, dto);
  }

  /**
   * Joins each LocalTransaction with its 1:1 OutboxRecord to report
   * outboxStatus/cloudTransactionId - the only sync-related signal this
   * schema actually has. Deliberately does NOT include an
   * ACCEPTED/HOLD/REJECTED field: local_transactions/outbox_records has no
   * such column, and CloudSendResult never carries the cloud's
   * quality-validation outcome back to the gateway today (see
   * docs/gateway-architecture.md's "Local vs. cloud responsibility"
   * section for the honest statement of this gap). Inventing that field
   * here would violate the explicit "do not invent data SQLite does not
   * currently contain" instruction.
   */
  private async toSummaryDtos(transactions: LocalTransaction[]): Promise<LocalReceptionSummaryDto[]> {
    const result: LocalReceptionSummaryDto[] = [];
    for (const tx of transactions) {
      const outbox: OutboxRecord | null = await this.storage.getOutboxRecordByLocalTransactionId(tx.id);
      result.push({
        id: tx.id,
        localIdempotencyKey: tx.localIdempotencyKey,
        sourceId: tx.sourceId,
        vehicleId: tx.vehicleId,
        quantityKg: tx.quantityKg,
        fat: tx.fat,
        snf: tx.snf,
        temperature: tx.temperature,
        capturedAt: tx.capturedAt,
        createdAt: tx.createdAt,
        outboxStatus: outbox?.status ?? "PENDING",
        cloudTransactionId: tx.cloudTransactionId,
      });
    }
    return result;
  }
}
