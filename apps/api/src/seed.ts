/**
 * Seed script for local development / demo / e2e tests.
 *
 * Seeds:
 *  - The permission codes from @cc-mc/shared-types
 *  - Three MVP roles (Operator, Manager, Admin) - a defensible default
 *    subset of the BRD's seven illustrative roles (Section 5), matching
 *    tonight's explicit scope. See docs/assumptions.md #role-set.
 *  - Two chilling centres, so cross-centre isolation is demonstrable.
 *  - Four users: one Admin (all-centres), one Manager and one Operator at
 *    Centre 1, and a second Operator at Centre 2 specifically so
 *    cross-centre denial has something concrete to test against.
 *  - Global quality rules for FAT / SNF / TEMPERATURE.
 *  - A couple of sample sources/vehicles per centre so Reception is
 *    immediately exercisable after seeding.
 *
 * All seed passwords are for local development only - never use these in
 * a deployed environment. Exported as `seed(dataSource)` so both the CLI
 * entrypoint below and the e2e test global-setup can reuse the same logic
 * against different databases.
 */
import { DataSource } from "typeorm";
import * as bcrypt from "bcrypt";
import { PERMISSIONS, QualityParameter, RecordStatus } from "@cc-mc/shared-types";
import { ChillingCentre } from "./centres/entities/chilling-centre.entity";
import { User } from "./auth/entities/user.entity";
import { Role } from "./rbac/entities/role.entity";
import { Permission } from "./rbac/entities/permission.entity";
import { RolePermission } from "./rbac/entities/role-permission.entity";
import { UserRole } from "./rbac/entities/user-role.entity";
import { UserCentreAssignment } from "./rbac/entities/user-centre-assignment.entity";
import { QualityRule } from "./quality-rules/entities/quality-rule.entity";
import { Source } from "./sources/entities/source.entity";
import { Vehicle } from "./vehicles/entities/vehicle.entity";

const ROLE_PERMISSIONS: Record<string, string[]> = {
  Operator: [
    PERMISSIONS.SOURCE_VIEW,
    PERMISSIONS.VEHICLE_VIEW,
    PERMISSIONS.QUALITY_RULE_VIEW,
    PERMISSIONS.RECEPTION_VIEW,
    PERMISSIONS.RECEPTION_CREATE,
    PERMISSIONS.DASHBOARD_VIEW,
  ],
  Manager: [
    PERMISSIONS.SOURCE_VIEW,
    PERMISSIONS.SOURCE_CREATE,
    PERMISSIONS.SOURCE_EDIT,
    PERMISSIONS.VEHICLE_VIEW,
    PERMISSIONS.VEHICLE_CREATE,
    PERMISSIONS.VEHICLE_EDIT,
    PERMISSIONS.QUALITY_RULE_VIEW,
    PERMISSIONS.QUALITY_RULE_CONFIGURE,
    PERMISSIONS.RECEPTION_VIEW,
    PERMISSIONS.RECEPTION_CREATE,
    PERMISSIONS.RECEPTION_OVERRIDE,
    PERMISSIONS.DASHBOARD_VIEW,
    PERMISSIONS.AUDIT_VIEW,
  ],
  Admin: Object.values(PERMISSIONS),
};

export async function seed(dataSource: DataSource): Promise<void> {
  const permissionRepo = dataSource.getRepository(Permission);
  const roleRepo = dataSource.getRepository(Role);
  const rolePermissionRepo = dataSource.getRepository(RolePermission);
  const centreRepo = dataSource.getRepository(ChillingCentre);
  const userRepo = dataSource.getRepository(User);
  const userRoleRepo = dataSource.getRepository(UserRole);
  const centreAssignmentRepo = dataSource.getRepository(UserCentreAssignment);
  const ruleRepo = dataSource.getRepository(QualityRule);
  const sourceRepo = dataSource.getRepository(Source);
  const vehicleRepo = dataSource.getRepository(Vehicle);

  // --- Permissions -----------------------------------------------------
  for (const code of Object.values(PERMISSIONS)) {
    const existing = await permissionRepo.findOneBy({ code });
    if (existing) continue;
    const [module, ...actionParts] = code.split("_");
    await permissionRepo.save(permissionRepo.create({ code, module, action: actionParts.join("_") }));
  }

  // --- Roles + role-permission mapping -----------------------------------
  for (const [roleName, codes] of Object.entries(ROLE_PERMISSIONS)) {
    let role = await roleRepo.findOneBy({ name: roleName });
    if (!role) {
      role = await roleRepo.save(roleRepo.create({ name: roleName, isSystemDefault: true }));
    }
    for (const code of codes) {
      const permission = await permissionRepo.findOneByOrFail({ code });
      const exists = await rolePermissionRepo.findOneBy({ roleId: role.id, permissionId: permission.id });
      if (!exists) {
        await rolePermissionRepo.save(rolePermissionRepo.create({ roleId: role.id, permissionId: permission.id }));
      }
    }
  }

  // --- Centres -------------------------------------------------------
  const upsertCentre = async (code: string, name: string) => {
    let centre = await centreRepo.findOneBy({ code });
    if (!centre) {
      centre = await centreRepo.save(centreRepo.create({ code, name, status: RecordStatus.ACTIVE }));
    }
    return centre;
  };
  const centreBlr = await upsertCentre("BLR-CC-01", "Chilling Centre - Bangalore");
  const centreMys = await upsertCentre("MYS-CC-01", "Chilling Centre - Mysore");

  // --- Users -----------------------------------------------------------
  const hash = (plain: string) => bcrypt.hash(plain, 10);

  const adminRole = await roleRepo.findOneByOrFail({ name: "Admin" });
  const managerRole = await roleRepo.findOneByOrFail({ name: "Manager" });
  const operatorRole = await roleRepo.findOneByOrFail({ name: "Operator" });

  const upsertUser = async (email: string, fullName: string, password: string) => {
    let user = await userRepo.findOneBy({ email });
    if (!user) {
      user = await userRepo.save(
        userRepo.create({ email, fullName, passwordHash: await hash(password), status: RecordStatus.ACTIVE }),
      );
    }
    return user;
  };

  const admin = await upsertUser("admin@ccmc.local", "System Administrator", "Admin@12345");
  const manager1 = await upsertUser("manager1@ccmc.local", "Bangalore Manager", "Manager@12345");
  const operator1 = await upsertUser("operator1@ccmc.local", "Bangalore Operator", "Operator@12345");
  const operator2 = await upsertUser("operator2@ccmc.local", "Mysore Operator", "Operator@12345");

  const assignRole = async (userId: number, roleId: number) => {
    const exists = await userRoleRepo.findOneBy({ userId, roleId });
    if (!exists) await userRoleRepo.save(userRoleRepo.create({ userId, roleId }));
  };
  await assignRole(admin.id, adminRole.id);
  await assignRole(manager1.id, managerRole.id);
  await assignRole(operator1.id, operatorRole.id);
  await assignRole(operator2.id, operatorRole.id);

  const assignCentre = async (userId: number, opts: { centreId?: number; allCentres?: boolean }) => {
    const centreId = opts.centreId ?? null;
    const exists = await centreAssignmentRepo.findOneBy({ userId, centreId: centreId as any });
    if (!exists) {
      await centreAssignmentRepo.save(
        centreAssignmentRepo.create({ userId, centreId, allCentres: opts.allCentres ?? false }),
      );
    }
  };
  await assignCentre(admin.id, { allCentres: true });
  await assignCentre(manager1.id, { centreId: centreBlr.id });
  await assignCentre(operator1.id, { centreId: centreBlr.id });
  await assignCentre(operator2.id, { centreId: centreMys.id });

  // --- Global quality rules -------------------------------------------
  const globalRules: Array<{ parameter: QualityParameter; minValue: number; maxValue: number }> = [
    { parameter: QualityParameter.FAT, minValue: 3.0, maxValue: 6.0 },
    { parameter: QualityParameter.SNF, minValue: 8.0, maxValue: 10.0 },
    { parameter: QualityParameter.TEMPERATURE, minValue: 0, maxValue: 10.0 },
  ];
  for (const rule of globalRules) {
    const exists = await ruleRepo.findOneBy({ parameter: rule.parameter, centreId: null as any });
    if (!exists) {
      await ruleRepo.save(
        ruleRepo.create({
          parameter: rule.parameter,
          minValue: String(rule.minValue),
          maxValue: String(rule.maxValue),
          centreId: null,
        }),
      );
    }
  }

  // --- Sample master data so Reception is immediately exercisable ------
  const upsertSource = async (code: string, name: string, centreId: number) => {
    const exists = await sourceRepo.findOneBy({ code });
    if (!exists) {
      await sourceRepo.save(
        sourceRepo.create({
          code,
          name,
          location: null,
          contact: null,
          milkType: "Cow",
          centreId,
          status: RecordStatus.ACTIVE,
        }),
      );
    }
  };
  await upsertSource("SRC-BLR-001", "Collection Centre A", centreBlr.id);
  await upsertSource("SRC-MYS-001", "Collection Centre B", centreMys.id);

  const upsertVehicle = async (vehicleNumber: string, driverName: string, centreId: number) => {
    const exists = await vehicleRepo.findOneBy({ vehicleNumber });
    if (!exists) {
      await vehicleRepo.save(
        vehicleRepo.create({
          vehicleNumber,
          tankerNumber: `TNK-${vehicleNumber.slice(-4)}`,
          driverName,
          driverMobile: "9900000000",
          capacityKg: "5000",
          centreId,
          status: RecordStatus.ACTIVE,
        }),
      );
    }
  };
  await upsertVehicle("KA01AB1234", "Ramesh", centreBlr.id);
  await upsertVehicle("KA09CD5678", "Suresh", centreMys.id);
}

// CLI entrypoint: `pnpm seed` (see package.json), runs against DATABASE_URL.
if (require.main === module) {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  require("./env");
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const { AppDataSource } = require("./data-source");

  AppDataSource.initialize()
    .then(async (ds: DataSource) => {
      await seed(ds);
      // eslint-disable-next-line no-console
      console.log("Seed complete.");
      // eslint-disable-next-line no-console
      console.log("Seeded users: admin@ccmc.local / Admin@12345 (Admin, all centres)");
      // eslint-disable-next-line no-console
      console.log("              manager1@ccmc.local / Manager@12345 (Manager, Bangalore)");
      // eslint-disable-next-line no-console
      console.log("              operator1@ccmc.local / Operator@12345 (Operator, Bangalore)");
      // eslint-disable-next-line no-console
      console.log("              operator2@ccmc.local / Operator@12345 (Operator, Mysore)");
      await ds.destroy();
    })
    .catch((err: unknown) => {
      // eslint-disable-next-line no-console
      console.error(err);
      process.exit(1);
    });
}
