import { Body, Controller, Get, Param, ParseIntPipe, Patch, Post, UseGuards } from "@nestjs/common";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { SourcesService } from "./sources.service";
import { CreateSourceDto } from "./dto/create-source.dto";
import { UpdateSourceDto } from "./dto/update-source.dto";

@Controller("sources")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class SourcesController {
  constructor(private readonly sources: SourcesService) {}

  @Get()
  @RequirePermission(PERMISSIONS.SOURCE_VIEW)
  list(@CurrentUser() user: RequestUser) {
    return this.sources.list(user);
  }

  @Get(":id")
  @RequirePermission(PERMISSIONS.SOURCE_VIEW)
  getById(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number) {
    return this.sources.getById(user, id);
  }

  @Post()
  @RequirePermission(PERMISSIONS.SOURCE_CREATE)
  create(@CurrentUser() user: RequestUser, @Body() dto: CreateSourceDto) {
    return this.sources.create(user, dto);
  }

  @Patch(":id")
  @RequirePermission(PERMISSIONS.SOURCE_EDIT)
  update(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number, @Body() dto: UpdateSourceDto) {
    return this.sources.update(user, id, dto);
  }
}
