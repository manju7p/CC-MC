import { BadRequestException, Controller, Get, Query, UseGuards } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { CentreAccessService } from "../rbac/centre-access.service";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { AuditLog } from "./entities/audit-log.entity";

@Controller("audit-logs")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class AuditController {
  constructor(
    @InjectRepository(AuditLog) private readonly auditRepo: Repository<AuditLog>,
    private readonly centreAccess: CentreAccessService,
  ) {}

  @Get()
  @RequirePermission(PERMISSIONS.AUDIT_VIEW)
  async list(
    @CurrentUser() user: RequestUser,
    @Query("resourceType") resourceType?: string,
    @Query("centreId") centreIdParam?: string,
  ) {
    const qb = this.auditRepo.createQueryBuilder("a").orderBy("a.createdAt", "DESC").take(200);

    if (!user.centreAccess.allCentres) {
      qb.andWhere("a.centreId IN (:...centreIds)", {
        centreIds: user.centreAccess.centreIds.length ? user.centreAccess.centreIds : [-1],
      });
    }
    if (centreIdParam) {
      const centreId = parseInt(centreIdParam, 10);
      if (Number.isNaN(centreId)) {
        throw new BadRequestException("centreId must be a number");
      }
      this.centreAccess.assertCanAccess(user.centreAccess, centreId);
      qb.andWhere("a.centreId = :centreId", { centreId });
    }
    if (resourceType) {
      qb.andWhere("a.resourceType = :resourceType", { resourceType });
    }

    return qb.getMany();
  }
}
