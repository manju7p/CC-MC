import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { ReceptionController } from "./reception.controller";
import { ReceptionService } from "./reception.service";
import { QualityValidationService } from "./quality-validation.service";
import { QualityRulesModule } from "../quality-rules/quality-rules.module";
import { MilkReceptionTransaction } from "./entities/milk-reception-transaction.entity";
import { TransactionOverride } from "./entities/transaction-override.entity";
import { ChillingCentre } from "../centres/entities/chilling-centre.entity";
import { Source } from "../sources/entities/source.entity";
import { Vehicle } from "../vehicles/entities/vehicle.entity";

@Module({
  imports: [
    TypeOrmModule.forFeature([MilkReceptionTransaction, TransactionOverride, ChillingCentre, Source, Vehicle]),
    QualityRulesModule,
  ],
  controllers: [ReceptionController],
  providers: [ReceptionService, QualityValidationService],
  exports: [ReceptionService, QualityValidationService],
})
export class ReceptionModule {}
