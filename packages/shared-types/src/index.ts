/**
 * @cc-mc/shared-types
 *
 * Types shared between apps/api and apps/web (and, later, apps/gateway).
 * Kept intentionally small for the MVP vertical slice — grow this file
 * only when a real second consumer needs the shape, not speculatively.
 */

// ---------------------------------------------------------------------------
// Permission codes
// ---------------------------------------------------------------------------
// Fine-grained permission codes per Engineering Rule 2 (backend-authoritative
// RBAC, no generic policy engine). This is the single source of truth for
// permission strings used by both the NestJS guards and the React UI's
// (non-authoritative) conditional rendering.

export const PERMISSIONS = {
  SOURCE_VIEW: "SOURCE_VIEW",
  SOURCE_CREATE: "SOURCE_CREATE",
  SOURCE_EDIT: "SOURCE_EDIT",

  VEHICLE_VIEW: "VEHICLE_VIEW",
  VEHICLE_CREATE: "VEHICLE_CREATE",
  VEHICLE_EDIT: "VEHICLE_EDIT",

  QUALITY_RULE_VIEW: "QUALITY_RULE_VIEW",
  QUALITY_RULE_CONFIGURE: "QUALITY_RULE_CONFIGURE",

  RECEPTION_VIEW: "RECEPTION_VIEW",
  RECEPTION_CREATE: "RECEPTION_CREATE",
  RECEPTION_OVERRIDE: "RECEPTION_OVERRIDE",

  DASHBOARD_VIEW: "DASHBOARD_VIEW",
  AUDIT_VIEW: "AUDIT_VIEW",
} as const;

export type PermissionCode = (typeof PERMISSIONS)[keyof typeof PERMISSIONS];

// ---------------------------------------------------------------------------
// Domain enums
// ---------------------------------------------------------------------------

export enum RecordStatus {
  ACTIVE = "ACTIVE",
  INACTIVE = "INACTIVE",
}

export enum TransactionStatus {
  ACCEPTED = "ACCEPTED",
  REJECTED = "REJECTED",
  HOLD = "HOLD",
}

export enum ReadingSource {
  MANUAL = "MANUAL",
  DEVICE = "DEVICE", // not reachable in the MVP slice; reserved for the gateway
}

export enum QualityParameter {
  FAT = "FAT",
  SNF = "SNF",
  TEMPERATURE = "TEMPERATURE",
}

// ---------------------------------------------------------------------------
// Auth
// ---------------------------------------------------------------------------

export interface LoginRequest {
  email: string;
  password: string;
}

export interface CentreAccessSummary {
  allCentres: boolean;
  centreIds: number[];
}

export interface AuthenticatedUser {
  id: number;
  email: string;
  fullName: string;
  roles: string[];
  permissions: PermissionCode[];
  centreAccess: CentreAccessSummary;
}

export interface LoginResponse {
  accessToken: string;
  user: AuthenticatedUser;
}

// ---------------------------------------------------------------------------
// Master data
// ---------------------------------------------------------------------------

export interface ChillingCentreDto {
  id: number;
  code: string;
  name: string;
  status: RecordStatus;
}

export interface SourceDto {
  id: number;
  code: string;
  name: string;
  location: string | null;
  contact: string | null;
  milkType: string | null;
  status: RecordStatus;
  centreId: number;
}

export interface VehicleDto {
  id: number;
  vehicleNumber: string;
  tankerNumber: string | null;
  driverName: string | null;
  driverMobile: string | null;
  capacityKg: number | null;
  status: RecordStatus;
  centreId: number;
}

// ---------------------------------------------------------------------------
// Quality rules
// ---------------------------------------------------------------------------

export interface QualityRuleDto {
  id: number;
  parameter: QualityParameter;
  minValue: number;
  maxValue: number;
  centreId: number | null; // null = global default rule
}

// ---------------------------------------------------------------------------
// Reception
// ---------------------------------------------------------------------------

export interface CreateReceptionRequest {
  centreId: number;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
  /**
   * Optional. Set by the Local Device Gateway (Checkpoint 5) so a retried
   * sync attempt (after a network failure, timeout, or lost response) is
   * safely idempotent server-side instead of creating a duplicate
   * transaction. Omitted entirely by the existing web create-reception
   * form - a request without this field behaves exactly as it always has.
   * See apps/api/src/reception/reception.service.ts's create() and
   * docs/gateway-architecture.md's cloud-sync section for the full
   * same-key/same-payload vs. same-key/different-payload semantics.
   */
  localIdempotencyKey?: string;
}

export interface ReceptionTransactionDto {
  id: number;
  transactionNumber: string;
  centreId: number;
  sourceId: number;
  vehicleId: number;
  operatorUserId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
  status: TransactionStatus;
  readingSource: ReadingSource;
  reason: string | null;
  receivedAt: string;
}

export interface OverrideReceptionRequest {
  newStatus: TransactionStatus.ACCEPTED | TransactionStatus.REJECTED;
  reason: string;
}

// ---------------------------------------------------------------------------
// Dashboard
// ---------------------------------------------------------------------------

export interface DashboardSummaryDto {
  date: string;
  centreIds: number[];
  totalTransactions: number;
  accepted: number;
  rejected: number;
  hold: number;
}

// ---------------------------------------------------------------------------
// Audit
// ---------------------------------------------------------------------------

export interface AuditLogDto {
  id: number;
  userId: number | null;
  centreId: number | null;
  action: string;
  resourceType: string;
  resourceId: string;
  oldValue: unknown;
  newValue: unknown;
  reason: string | null;
  createdAt: string;
}

// ---------------------------------------------------------------------------
// Local (edge) gateway API
// ---------------------------------------------------------------------------
// Consumed by apps/web's local dashboard (LocalDashboardPage.tsx) and
// produced by apps/gateway's local-api-server.ts - see
// docs/gateway-architecture.md's "Local vs. cloud responsibility" section
// for the full edge/offline-first architecture this supports. These types
// describe the gateway's LOCAL SQLite-backed operational view, which is
// deliberately NOT the same shape as the cloud's DashboardSummaryDto/
// ReceptionTransactionDto above - the local gateway has no concept of
// ACCEPTED/HOLD/REJECTED (local_transactions/outbox_records has no such
// column, and the cloud's quality-validation outcome is never sent back to
// the gateway today), so these DTOs honestly expose only outbox sync state
// (PENDING/PROCESSING/SYNCED/FAILED) instead of fabricating a status the
// local schema cannot support.

export type LocalOutboxStatus = "PENDING" | "PROCESSING" | "SYNCED" | "FAILED";

export type LocalServiceState = "STARTING" | "RUNNING" | "STOPPING" | "STOPPED";

export type LocalConnectivityState = "UNKNOWN" | "CONNECTED" | "DISCONNECTED";

/** Mirrors apps/gateway's GatewayHealthSnapshot - returned by GET /local/status. */
export interface LocalGatewayStatusDto {
  gatewayId: string;
  centreId: number;
  version: string;
  startedAt: string | null;
  uptimeSeconds: number;
  serviceState: LocalServiceState;
  cloudConnectivity: LocalConnectivityState;
  deviceConnectivity: LocalConnectivityState;
  pendingSyncCount: number;
}

/**
 * One locally-captured reception transaction, joined with its outbox sync
 * state. NOTE: deliberately has no `status` (ACCEPTED/HOLD/REJECTED) field
 * - see this section's header comment. `outboxStatus`/`cloudTransactionId`
 * are the only sync-related signals the local schema actually has.
 */
export interface LocalReceptionSummaryDto {
  id: number;
  localIdempotencyKey: string;
  sourceId: number;
  vehicleId: number;
  quantityKg: number;
  fat: number;
  snf: number;
  temperature: number;
  capturedAt: string;
  createdAt: string;
  outboxStatus: LocalOutboxStatus;
  cloudTransactionId: number | null;
}

/** Returned by GET /local/today - today's (IST calendar day) local collection summary. */
export interface LocalTodaySummaryDto {
  /** IST calendar date this summary covers, e.g. "2026-08-23". */
  dateLabel: string;
  totalTransactions: number;
  totalQuantityKg: number;
  transactions: LocalReceptionSummaryDto[];
}

/** Returned by GET /local/transactions?limit=N. */
export interface LocalTransactionsResponseDto {
  transactions: LocalReceptionSummaryDto[];
}
