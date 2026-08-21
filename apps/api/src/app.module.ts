import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { ALL_ENTITIES } from "./entities";
import { RbacModule } from "./rbac/rbac.module";
import { AuditModule } from "./audit/audit.module";
import { AuthModule } from "./auth/auth.module";
import { CentresModule } from "./centres/centres.module";
import { SourcesModule } from "./sources/sources.module";
import { VehiclesModule } from "./vehicles/vehicles.module";
import { QualityRulesModule } from "./quality-rules/quality-rules.module";
import { ReceptionModule } from "./reception/reception.module";
import { DashboardModule } from "./dashboard/dashboard.module";

@Module({
  imports: [
    TypeOrmModule.forRoot({
      type: "postgres",
      url: process.env.DATABASE_URL,
      entities: ALL_ENTITIES,
      synchronize: false, // schema is managed via migrations - see src/migrations
      migrationsRun: false,
    }),
    RbacModule,
    AuditModule,
    AuthModule,
    CentresModule,
    SourcesModule,
    VehiclesModule,
    QualityRulesModule,
    ReceptionModule,
    DashboardModule,
  ],
})
export class AppModule {}
