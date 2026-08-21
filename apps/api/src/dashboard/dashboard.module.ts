import { Module } from "@nestjs/common";
import { TypeOrmModule } from "@nestjs/typeorm";
import { DashboardController } from "./dashboard.controller";
import { DashboardService } from "./dashboard.service";
import { MilkReceptionTransaction } from "../reception/entities/milk-reception-transaction.entity";

@Module({
  imports: [TypeOrmModule.forFeature([MilkReceptionTransaction])],
  controllers: [DashboardController],
  providers: [DashboardService],
})
export class DashboardModule {}
