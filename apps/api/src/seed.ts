/**
 * Seed script for local development / demo / e2e tests.
 *
 * Seeds:
 *  - The permission codes from @cc-mc/shared-types
 *  - Three MVP roles (Operator, Manager, Admin) - a defensible default
 *    subset of the BRD's seven illustrative roles (Section 5), matching
 *    tonight's explicit scope. See docs/assumptions.md #role-set. Plus a
 *    fourth, machine-only role (GatewayService, Checkpoint 5) - see below.
 *  - Two chilling centres, so cross-centre isolation is demonstrable.
 *  - Four human users: one Admin (all-centres), one Manager and one
 *    Operator at Centre 1, and a second Operator at Centre 2 specifically
 *    so cross-centre denial has something concrete to test against.
 *  - Two gateway service-account users (Checkpoint 5), one per centre,
 *    RECEPTION_CREATE only - see the GatewayService role above.
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

  // Checkpoint 5: the Local Device Gateway's unattended service-account
  // role. Deliberately the smallest possible permission set - a gateway
  // only ever calls POST /reception, so RECEPTION_CREATE is the ONLY
  // permission it holds. No RECEPTION_VIEW, no DASHBOARD_VIEW, no
  // AUDIT_VIEW - a compromised or misbehaving gateway credential still
  // cannot read anything, and can only create receptions for the one
  // centre it's assigned to (see the UserCentreAssignment rows below;
  // CentreAccessService itself is completely unmodified for this role -
  // see docs/gateway-architecture.md's cloud-sync section for the full
  // design).
  GatewayService: [PERMISSIONS.RECEPTION_CREATE],
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

  // Checkpoint 5: one gateway service account PER CENTRE. Each is a
  // completely ordinary User row authenticated through the exact same
  // POST /auth/login endpoint and JwtStrategy every human user goes
  // through - see docs/gateway-architecture.md's cloud-sync section for
  // why this, rather than a parallel auth mechanism, is the design. Two
  // accounts (not one shared account) so a gateway installed at one
  // centre structurally cannot even present credentials that would
  // resolve to another centre's access - there is no "switch centre"
  // concept for a gateway account to abuse, unlike a human Admin.
  const gatewayServiceRole = await roleRepo.findOneByOrFail({ name: "GatewayService" });
  const gatewayBlr = await upsertUser(
    "gateway-blr-cc-01@ccmc.local",
    "Gateway Service Account - Bangalore (BLR-CC-01)",
    "GatewayBLR@2026!sync",
  );
  const gatewayMys = await upsertUser(
    "gateway-mys-cc-01@ccmc.local",
    "Gateway Service Account - Mysore (MYS-CC-01)",
    "GatewayMYS@2026!sync",
  );

  const assignRole = async (userId: number, roleId: number) => {
    const exists = await userRoleRepo.findOneBy({ userId, roleId });
    if (!exists) await userRoleRepo.save(userRoleRepo.create({ userId, roleId }));
  };
  await assignRole(admin.id, adminRole.id);
  await assignRole(manager1.id, managerRole.id);
  await assignRole(operator1.id, operatorRole.id);
  await assignRole(operator2.id, operatorRole.id);
  await assignRole(gatewayBlr.id, gatewayServiceRole.id);
  await assignRole(gatewayMys.id, gatewayServiceRole.id);

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
  // Never allCentres for a gateway account - exactly one centre each,
  // enforced the same way (CentreAccessService.assertCanAccess) as every
  // other centre-scoped user in this system. See
  // test/reception-idempotency.e2e-spec.ts's centre-isolation tests.
  await assignCentre(gatewayBlr.id, { centreId: centreBlr.id });
  await assignCentre(gatewayMys.id, { centreId: centreMys.id });

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
      // eslint-disable-next-line no-console
      console.log(
        "              gateway-blr-cc-01@ccmc.local / GatewayBLR@2026!sync (GatewayService, Bangalore only)",
      );
      // eslint-disable-next-line no-console
      console.log(
        "              gateway-mys-cc-01@ccmc.local / GatewayMYS@2026!sync (GatewayService, Mysore only)",
      );
      await ds.destroy();
    })
    .catch((err: unknown) => {
      // eslint-disable-next-line no-console
      console.error(err);
      process.exit(1);
    });
}
