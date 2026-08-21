import { Body, Controller, Get, Param, ParseIntPipe, Post, UseGuards } from "@nestjs/common";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { ReceptionService } from "./reception.service";
import { CreateReceptionDto } from "./dto/create-reception.dto";
import { OverrideReceptionDto } from "./dto/override-reception.dto";

@Controller("reception")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class ReceptionController {
  constructor(private readonly reception: ReceptionService) {}

  @Get()
  @RequirePermission(PERMISSIONS.RECEPTION_VIEW)
  list(@CurrentUser() user: RequestUser) {
    return this.reception.list(user);
  }

  @Get(":id")
  @RequirePermission(PERMISSIONS.RECEPTION_VIEW)
  getById(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number) {
    return this.reception.getById(user, id);
  }

  @Post()
  @RequirePermission(PERMISSIONS.RECEPTION_CREATE)
  create(@CurrentUser() user: RequestUser, @Body() dto: CreateReceptionDto) {
    return this.reception.create(user, dto);
  }

  @Post(":id/override")
  @RequirePermission(PERMISSIONS.RECEPTION_OVERRIDE)
  override(
    @CurrentUser() user: RequestUser,
    @Param("id", ParseIntPipe) id: number,
    @Body() dto: OverrideReceptionDto,
  ) {
    return this.reception.override(user, id, dto);
  }
}
