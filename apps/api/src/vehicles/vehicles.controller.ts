import { Body, Controller, Get, Param, ParseIntPipe, Patch, Post, UseGuards } from "@nestjs/common";
import { PERMISSIONS } from "@cc-mc/shared-types";
import { JwtAuthGuard } from "../auth/jwt-auth.guard";
import { PermissionGuard } from "../rbac/permission.guard";
import { RequirePermission } from "../rbac/permissions.decorator";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";
import { VehiclesService } from "./vehicles.service";
import { CreateVehicleDto } from "./dto/create-vehicle.dto";
import { UpdateVehicleDto } from "./dto/update-vehicle.dto";

@Controller("vehicles")
@UseGuards(JwtAuthGuard, PermissionGuard)
export class VehiclesController {
  constructor(private readonly vehicles: VehiclesService) {}

  @Get()
  @RequirePermission(PERMISSIONS.VEHICLE_VIEW)
  list(@CurrentUser() user: RequestUser) {
    return this.vehicles.list(user);
  }

  @Get(":id")
  @RequirePermission(PERMISSIONS.VEHICLE_VIEW)
  getById(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number) {
    return this.vehicles.getById(user, id);
  }

  @Post()
  @RequirePermission(PERMISSIONS.VEHICLE_CREATE)
  create(@CurrentUser() user: RequestUser, @Body() dto: CreateVehicleDto) {
    return this.vehicles.create(user, dto);
  }

  @Patch(":id")
  @RequirePermission(PERMISSIONS.VEHICLE_EDIT)
  update(@CurrentUser() user: RequestUser, @Param("id", ParseIntPipe) id: number, @Body() dto: UpdateVehicleDto) {
    return this.vehicles.update(user, id, dto);
  }
}
