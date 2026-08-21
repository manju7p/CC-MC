import { BadRequestException, Controller, Get, Query, UseGuards } from "@nestjs/common";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { DashboardService } from "./dashboard.service";

@Controller("dashboard")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class DashboardController {
  constructor(private readonly dashboard: DashboardService) {}

  @Get("summary")
  @RequirePermission(PERMISSIONS.DASHBOARD_VIEW)
  summary(@CurrentUser() user: RequestUser, @Query("centreId") centreIdParam?: string) {
    let centreIdFilter: number | undefined;
    if (centreIdParam !== undefined) {
      centreIdFilter = parseInt(centreIdParam, 10);
      // A non-numeric ?centreId= must be rejected outright, not silently
      // treated as "no filter" or as centre NaN (which would previously
      // return an empty summary for an org-wide user, or a confusing 403
      // for a scoped one, instead of a clear 400).
      if (Number.isNaN(centreIdFilter)) {
        throw new BadRequestException("centreId must be a number");
      }
    }
    return this.dashboard.summary(user.centreAccess, centreIdFilter);
  }
}
