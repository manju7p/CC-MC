import { Body, Controller, Get, Param, ParseIntPipe, Patch, UseGuards } from "@nestjs/common";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { QualityRulesService } from "./quality-rules.service";
import { UpdateQualityRuleDto } from "./dto/update-quality-rule.dto";

@Controller("quality-rules")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class QualityRulesController {
  constructor(private readonly qualityRules: QualityRulesService) {}

  @Get()
  @RequirePermission(PERMISSIONS.QUALITY_RULE_VIEW)
  list(@CurrentUser() user: RequestUser) {
    return this.qualityRules.list(user);
  }

  @Patch(":id")
  @RequirePermission(PERMISSIONS.QUALITY_RULE_CONFIGURE)
  update(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number, @Body() dto: UpdateQualityRuleDto) {
    return this.qualityRules.update(user, id, dto);
  }
}
