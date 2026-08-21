import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { CentresController } from "./centres.controller";
import { ChillingCentre } from "./entities/chilling-centre.entity";

@Module({
  imports: [TypeOrmModule.forFeature([ChillingCentre])],
  controllers: [CentresController],
})
export class CentresModule {}
