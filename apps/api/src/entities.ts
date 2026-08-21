/**
 * Central list of all TypeORM entities, used by both the Nest app
 * (app.module.ts) and the CLI DataSource (data-source.ts) so the two never
 * drift out of sync.
 */
import { ChillingCentre } from "./centres/entities/chilling-centre.entity";
import { User } from "./auth/entities/user.entity";
import { Role } from "./rbac/entities/role.entity";
import { Permission } from "./rbac/entities/permission.entity";
import { RolePermission } from "./rbac/entities/role-permission.entity";
import { UserRole } from "./rbac/entities/user-role.entity";
import { UserCentreAssignment } from "./rbac/entities/user-centre-assignment.entity";
import { Source } from "./sources/entities/source.entity";
import { Vehicle } from "./vehicles/entities/vehicle.entity";
import { QualityRule } from "./quality-rules/entities/quality-rule.entity";
import { MilkReceptionTransaction } from "./reception/entities/milk-reception-transaction.entity";
import { TransactionOverride } from "./reception/entities/transaction-override.entity";
import { AuditLog } from "./audit/entities/audit-log.entity";

export const ALL_ENTITIES = [
  ChillingCentre,
  User,
  Role,
  Permission,
  RolePermission,
  UserRole,
  UserCentreAssignment,
  Source,
  Vehicle,
  QualityRule,
  MilkReceptionTransaction,
  TransactionOverride,
  AuditLog,
];

export {
  ChillingCentre,
  User,
  Role,
  Permission,
  RolePermission,
  UserRole,
  UserCentreAssignment,
  Source,
  Vehicle,
  QualityRule,
  MilkReceptionTransaction,
  TransactionOverride,
  AuditLog,
};
