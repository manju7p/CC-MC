import { MigrationInterface, QueryRunner } from "typeorm";

export class InitSchema1787224364198 implements MigrationInterface {
    name = 'InitSchema1787224364198'

    public async up(queryRunner: QueryRunner): Promise<void> {
        await queryRunner.query(`CREATE TYPE "public"."chilling_centres_status_enum" AS ENUM('ACTIVE', 'INACTIVE')`);
        await queryRunner.query(`CREATE TABLE "chilling_centres" ("id" SERIAL NOT NULL, "code" character varying NOT NULL, "name" character varying NOT NULL, "status" "public"."chilling_centres_status_enum" NOT NULL DEFAULT 'ACTIVE', "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "updatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "UQ_19a219cde22f67bdbcda9674d2c" UNIQUE ("code"), CONSTRAINT "PK_46bdd1f22408a6388e0b90081f0" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TYPE "public"."users_status_enum" AS ENUM('ACTIVE', 'INACTIVE')`);
        await queryRunner.query(`CREATE TABLE "users" ("id" SERIAL NOT NULL, "email" character varying NOT NULL, "passwordHash" character varying NOT NULL, "fullName" character varying NOT NULL, "status" "public"."users_status_enum" NOT NULL DEFAULT 'ACTIVE', "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "updatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "UQ_97672ac88f789774dd47f7c8be3" UNIQUE ("email"), CONSTRAINT "PK_a3ffb1c0c8416b9fc6f907b7433" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TABLE "roles" ("id" SERIAL NOT NULL, "name" character varying NOT NULL, "isSystemDefault" boolean NOT NULL DEFAULT true, CONSTRAINT "UQ_648e3f5447f725579d7d4ffdfb7" UNIQUE ("name"), CONSTRAINT "PK_c1433d71a4838793a49dcad46ab" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TABLE "permissions" ("id" SERIAL NOT NULL, "code" character varying NOT NULL, "module" character varying NOT NULL, "action" character varying NOT NULL, CONSTRAINT "UQ_8dad765629e83229da6feda1c1d" UNIQUE ("code"), CONSTRAINT "PK_920331560282b8bd21bb02290df" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TABLE "role_permissions" ("roleId" integer NOT NULL, "permissionId" integer NOT NULL, CONSTRAINT "PK_d430a02aad006d8a70f3acd7d03" PRIMARY KEY ("roleId", "permissionId"))`);
        await queryRunner.query(`CREATE TABLE "user_roles" ("userId" integer NOT NULL, "roleId" integer NOT NULL, CONSTRAINT "PK_88481b0c4ed9ada47e9fdd67475" PRIMARY KEY ("userId", "roleId"))`);
        await queryRunner.query(`CREATE TABLE "user_centre_assignments" ("id" SERIAL NOT NULL, "userId" integer NOT NULL, "centreId" integer, "allCentres" boolean NOT NULL DEFAULT false, CONSTRAINT "UQ_693b91e3f14630846a0dca15bb9" UNIQUE ("userId", "centreId"), CONSTRAINT "PK_6310e45ac742472367a41336166" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TYPE "public"."sources_status_enum" AS ENUM('ACTIVE', 'INACTIVE')`);
        await queryRunner.query(`CREATE TABLE "sources" ("id" SERIAL NOT NULL, "code" character varying NOT NULL, "name" character varying NOT NULL, "location" character varying, "contact" character varying, "milkType" character varying, "status" "public"."sources_status_enum" NOT NULL DEFAULT 'ACTIVE', "centreId" integer NOT NULL, "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "updatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "UQ_fbcaa228d0b384ae42c2d06ab3d" UNIQUE ("code"), CONSTRAINT "PK_85523beafe5a2a6b90b02096443" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TYPE "public"."vehicles_status_enum" AS ENUM('ACTIVE', 'INACTIVE')`);
        await queryRunner.query(`CREATE TABLE "vehicles" ("id" SERIAL NOT NULL, "vehicleNumber" character varying NOT NULL, "tankerNumber" character varying, "driverName" character varying, "driverMobile" character varying, "capacityKg" numeric(10,2), "status" "public"."vehicles_status_enum" NOT NULL DEFAULT 'ACTIVE', "centreId" integer NOT NULL, "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "updatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "UQ_6165f51ba0c8c31b6ab41838e36" UNIQUE ("vehicleNumber"), CONSTRAINT "PK_18d8646b59304dce4af3a9e35b6" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TYPE "public"."quality_rules_parameter_enum" AS ENUM('FAT', 'SNF', 'TEMPERATURE')`);
        await queryRunner.query(`CREATE TABLE "quality_rules" ("id" SERIAL NOT NULL, "parameter" "public"."quality_rules_parameter_enum" NOT NULL, "minValue" numeric(6,2) NOT NULL, "maxValue" numeric(6,2) NOT NULL, "centreId" integer, CONSTRAINT "UQ_d2e58e91694fc05276542bf0b57" UNIQUE ("parameter", "centreId"), CONSTRAINT "PK_05a6caedab1374ee2324edb4ef2" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TYPE "public"."milk_reception_transactions_status_enum" AS ENUM('ACCEPTED', 'REJECTED', 'HOLD')`);
        await queryRunner.query(`CREATE TYPE "public"."milk_reception_transactions_readingsource_enum" AS ENUM('MANUAL', 'DEVICE')`);
        await queryRunner.query(`CREATE TABLE "milk_reception_transactions" ("id" SERIAL NOT NULL, "transactionNumber" character varying NOT NULL, "centreId" integer NOT NULL, "sourceId" integer NOT NULL, "vehicleId" integer NOT NULL, "operatorUserId" integer NOT NULL, "quantityKg" numeric(10,2) NOT NULL, "fat" numeric(5,2) NOT NULL, "snf" numeric(5,2) NOT NULL, "temperature" numeric(5,2) NOT NULL, "status" "public"."milk_reception_transactions_status_enum" NOT NULL, "readingSource" "public"."milk_reception_transactions_readingsource_enum" NOT NULL DEFAULT 'MANUAL', "reason" character varying, "localIdempotencyKey" character varying, "receivedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), "updatedAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "UQ_3299b1a807067d5b088d8a594e9" UNIQUE ("transactionNumber"), CONSTRAINT "UQ_48e8713b2f5b3c9673e75615cfe" UNIQUE ("localIdempotencyKey"), CONSTRAINT "PK_a9879d02d207d41836c2295b3a5" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE INDEX "IDX_915e1a12e7ec1b14bda6f99602" ON "milk_reception_transactions" ("centreId", "status") `);
        await queryRunner.query(`CREATE INDEX "IDX_1dffc08a199bef832dc5e65727" ON "milk_reception_transactions" ("centreId", "receivedAt") `);
        await queryRunner.query(`CREATE TYPE "public"."transaction_overrides_originalstatus_enum" AS ENUM('ACCEPTED', 'REJECTED', 'HOLD')`);
        await queryRunner.query(`CREATE TYPE "public"."transaction_overrides_newstatus_enum" AS ENUM('ACCEPTED', 'REJECTED', 'HOLD')`);
        await queryRunner.query(`CREATE TABLE "transaction_overrides" ("id" SERIAL NOT NULL, "transactionId" integer NOT NULL, "originalStatus" "public"."transaction_overrides_originalstatus_enum" NOT NULL, "newStatus" "public"."transaction_overrides_newstatus_enum" NOT NULL, "performedByUserId" integer NOT NULL, "reason" character varying NOT NULL, "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "PK_dddbdda550e55f90b7ea6c79610" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE TABLE "audit_logs" ("id" SERIAL NOT NULL, "userId" integer, "centreId" integer, "action" character varying NOT NULL, "resourceType" character varying NOT NULL, "resourceId" character varying NOT NULL, "oldValue" jsonb, "newValue" jsonb, "reason" character varying, "createdAt" TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT now(), CONSTRAINT "PK_1bb179d048bbc581caa3b013439" PRIMARY KEY ("id"))`);
        await queryRunner.query(`CREATE INDEX "IDX_99e589da8f9e9326ee0d01a028" ON "audit_logs" ("userId", "createdAt") `);
        await queryRunner.query(`CREATE INDEX "IDX_8e229d453b21312155c6ab8cfd" ON "audit_logs" ("resourceType", "resourceId") `);
        await queryRunner.query(`ALTER TABLE "role_permissions" ADD CONSTRAINT "FK_b4599f8b8f548d35850afa2d12c" FOREIGN KEY ("roleId") REFERENCES "roles"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "role_permissions" ADD CONSTRAINT "FK_06792d0c62ce6b0203c03643cdd" FOREIGN KEY ("permissionId") REFERENCES "permissions"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "user_roles" ADD CONSTRAINT "FK_472b25323af01488f1f66a06b67" FOREIGN KEY ("userId") REFERENCES "users"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "user_roles" ADD CONSTRAINT "FK_86033897c009fcca8b6505d6be2" FOREIGN KEY ("roleId") REFERENCES "roles"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "user_centre_assignments" ADD CONSTRAINT "FK_52dd1d627ef48b4bc093d185f09" FOREIGN KEY ("userId") REFERENCES "users"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "user_centre_assignments" ADD CONSTRAINT "FK_2fddd94f9b19fcfdb8237683bc0" FOREIGN KEY ("centreId") REFERENCES "chilling_centres"("id") ON DELETE CASCADE ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "sources" ADD CONSTRAINT "FK_53cd4e4f8b1c9e13e3824b9d278" FOREIGN KEY ("centreId") REFERENCES "chilling_centres"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "vehicles" ADD CONSTRAINT "FK_fa306142c55e2b80b77f91ae34a" FOREIGN KEY ("centreId") REFERENCES "chilling_centres"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "quality_rules" ADD CONSTRAINT "FK_59cdd83b8c53d5159d819e546f7" FOREIGN KEY ("centreId") REFERENCES "chilling_centres"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" ADD CONSTRAINT "FK_8562cf9b895188e5ec147dd1a7c" FOREIGN KEY ("centreId") REFERENCES "chilling_centres"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" ADD CONSTRAINT "FK_fb9acf21cd2deb5682cd7f28662" FOREIGN KEY ("sourceId") REFERENCES "sources"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" ADD CONSTRAINT "FK_a29ed70f21f55f92d76eae0d3be" FOREIGN KEY ("vehicleId") REFERENCES "vehicles"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" ADD CONSTRAINT "FK_aba86b74d884924b66a81de01e9" FOREIGN KEY ("operatorUserId") REFERENCES "users"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "transaction_overrides" ADD CONSTRAINT "FK_b43f7d3693978550639ced2b60f" FOREIGN KEY ("transactionId") REFERENCES "milk_reception_transactions"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
        await queryRunner.query(`ALTER TABLE "transaction_overrides" ADD CONSTRAINT "FK_5b813c99a4186566fec07fe75a2" FOREIGN KEY ("performedByUserId") REFERENCES "users"("id") ON DELETE NO ACTION ON UPDATE NO ACTION`);
    }

    public async down(queryRunner: QueryRunner): Promise<void> {
        await queryRunner.query(`ALTER TABLE "transaction_overrides" DROP CONSTRAINT "FK_5b813c99a4186566fec07fe75a2"`);
        await queryRunner.query(`ALTER TABLE "transaction_overrides" DROP CONSTRAINT "FK_b43f7d3693978550639ced2b60f"`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" DROP CONSTRAINT "FK_aba86b74d884924b66a81de01e9"`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" DROP CONSTRAINT "FK_a29ed70f21f55f92d76eae0d3be"`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" DROP CONSTRAINT "FK_fb9acf21cd2deb5682cd7f28662"`);
        await queryRunner.query(`ALTER TABLE "milk_reception_transactions" DROP CONSTRAINT "FK_8562cf9b895188e5ec147dd1a7c"`);
        await queryRunner.query(`ALTER TABLE "quality_rules" DROP CONSTRAINT "FK_59cdd83b8c53d5159d819e546f7"`);
        await queryRunner.query(`ALTER TABLE "vehicles" DROP CONSTRAINT "FK_fa306142c55e2b80b77f91ae34a"`);
        await queryRunner.query(`ALTER TABLE "sources" DROP CONSTRAINT "FK_53cd4e4f8b1c9e13e3824b9d278"`);
        await queryRunner.query(`ALTER TABLE "user_centre_assignments" DROP CONSTRAINT "FK_2fddd94f9b19fcfdb8237683bc0"`);
        await queryRunner.query(`ALTER TABLE "user_centre_assignments" DROP CONSTRAINT "FK_52dd1d627ef48b4bc093d185f09"`);
        await queryRunner.query(`ALTER TABLE "user_roles" DROP CONSTRAINT "FK_86033897c009fcca8b6505d6be2"`);
        await queryRunner.query(`ALTER TABLE "user_roles" DROP CONSTRAINT "FK_472b25323af01488f1f66a06b67"`);
        await queryRunner.query(`ALTER TABLE "role_permissions" DROP CONSTRAINT "FK_06792d0c62ce6b0203c03643cdd"`);
        await queryRunner.query(`ALTER TABLE "role_permissions" DROP CONSTRAINT "FK_b4599f8b8f548d35850afa2d12c"`);
        await queryRunner.query(`DROP INDEX "public"."IDX_8e229d453b21312155c6ab8cfd"`);
        await queryRunner.query(`DROP INDEX "public"."IDX_99e589da8f9e9326ee0d01a028"`);
        await queryRunner.query(`DROP TABLE "audit_logs"`);
        await queryRunner.query(`DROP TABLE "transaction_overrides"`);
        await queryRunner.query(`DROP TYPE "public"."transaction_overrides_newstatus_enum"`);
        await queryRunner.query(`DROP TYPE "public"."transaction_overrides_originalstatus_enum"`);
        await queryRunner.query(`DROP INDEX "public"."IDX_1dffc08a199bef832dc5e65727"`);
        await queryRunner.query(`DROP INDEX "public"."IDX_915e1a12e7ec1b14bda6f99602"`);
        await queryRunner.query(`DROP TABLE "milk_reception_transactions"`);
        await queryRunner.query(`DROP TYPE "public"."milk_reception_transactions_readingsource_enum"`);
        await queryRunner.query(`DROP TYPE "public"."milk_reception_transactions_status_enum"`);
        await queryRunner.query(`DROP TABLE "quality_rules"`);
        await queryRunner.query(`DROP TYPE "public"."quality_rules_parameter_enum"`);
        await queryRunner.query(`DROP TABLE "vehicles"`);
        await queryRunner.query(`DROP TYPE "public"."vehicles_status_enum"`);
        await queryRunner.query(`DROP TABLE "sources"`);
        await queryRunner.query(`DROP TYPE "public"."sources_status_enum"`);
        await queryRunner.query(`DROP TABLE "user_centre_assignments"`);
        await queryRunner.query(`DROP TABLE "user_roles"`);
        await queryRunner.query(`DROP TABLE "role_permissions"`);
        await queryRunner.query(`DROP TABLE "permissions"`);
        await queryRunner.query(`DROP TABLE "roles"`);
        await queryRunner.query(`DROP TABLE "users"`);
        await queryRunner.query(`DROP TYPE "public"."users_status_enum"`);
        await queryRunner.query(`DROP TABLE "chilling_centres"`);
        await queryRunner.query(`DROP TYPE "public"."chilling_centres_status_enum"`);
    }

}
