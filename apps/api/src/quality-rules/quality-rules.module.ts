import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { QualityRulesController } from "./quality-rules.controller";
import { QualityRulesService } from "./quality-rules.service";
import { QualityRule } from "./entities/quality-rule.entity";

@Module({
  imports: [TypeOrmModule.forFeature([QualityRule])],
  controllers: [QualityRulesController],
  providers: [QualityRulesService],
  exports: [QualityRulesService],
})
export class QualityRulesModule {}
