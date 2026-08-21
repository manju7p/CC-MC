import { Controller, Get, UseGuards } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { CentreAccessService } from "../rbac/centre-access.service";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { ChillingCentre } from "./entities/chilling-centre.entity";

/**
 * Reference-data endpoint (which centres can this user act within). No
 * fine-grained permission is required beyond being authenticated - there is
 * no separate "centre management" capability in tonight's scope.
 */
@Controller("centres")
@UseGuards(JwtAuthGuard)
export class CentresController {
  constructor(
    @InjectRepository(ChillingCentre) private readonly centreRepo: Repository<ChillingCentre>,
    private readonly centreAccess: CentreAccessService,
  ) {}

  @Get()
  async list(@CurrentUser() user: RequestUser) {
    if (user.centreAccess.allCentres) {
      return this.centreRepo.find({ order: { name: "ASC" } });
    }
    if (user.centreAccess.centreIds.length === 0) {
      return [];
    }
    return this.centreRepo
      .createQueryBuilder("c")
      .where("c.id IN (:...ids)", { ids: user.centreAccess.centreIds })
      .orderBy("c.name", "ASC")
      .getMany();
  }
}
