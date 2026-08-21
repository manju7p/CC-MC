import { Injectable, NotFoundException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";
import { Vehicle } from "./entities/vehicle.entity";
import { CentreAccessService } from "../rbac/centre-access.service";
import { AuditService } from "../audit/audit.service";
import type { RequestUser } from "../rbac/rbac.types";
import { CreateVehicleDto } from "./dto/create-vehicle.dto";
import { UpdateVehicleDto } from "./dto/update-vehicle.dto";

@Injectable()
export class VehiclesService {
  constructor(
    @InjectRepository(Vehicle) private readonly vehicleRepo: Repository<Vehicle>,
    private readonly centreAccess: CentreAccessService,
    private readonly audit: AuditService,
  ) {}

  async list(user: RequestUser) {
    if (user.centreAccess.allCentres) {
      return this.vehicleRepo.find({ order: { vehicleNumber: "ASC" } });
    }
    if (user.centreAccess.centreIds.length === 0) return [];
    return this.vehicleRepo.find({
      where: user.centreAccess.centreIds.map((centreId) => ({ centreId })),
      order: { vehicleNumber: "ASC" },
    });
  }

  async getById(user: RequestUser, id: number) {
    const vehicle = await this.vehicleRepo.findOneBy({ id });
    if (!vehicle) throw new NotFoundException("Vehicle not found");
    this.centreAccess.assertCanAccess(user.centreAccess, vehicle.centreId);
    return vehicle;
  }

  async create(user: RequestUser, dto: CreateVehicleDto) {
    this.centreAccess.assertCanAccess(user.centreAccess, dto.centreId);

    const created = await this.vehicleRepo.save(
      this.vehicleRepo.create({
        centreId: dto.centreId,
        vehicleNumber: dto.vehicleNumber,
        tankerNumber: dto.tankerNumber ?? null,
        driverName: dto.driverName ?? null,
        driverMobile: dto.driverMobile ?? null,
        capacityKg: dto.capacityKg !== undefined ? String(dto.capacityKg) : null,
        status: dto.status ?? RecordStatus.ACTIVE,
      }),
    );

    await this.audit.record({
      userId: user.id,
      centreId: created.centreId,
      action: "VEHICLE_CREATE",
      resourceType: "Vehicle",
      resourceId: String(created.id),
      oldValue: null,
      newValue: created,
    });

    return created;
  }

  async update(user: RequestUser, id: number, dto: UpdateVehicleDto) {
    const existing = await this.vehicleRepo.findOneBy({ id });
    if (!existing) throw new NotFoundException("Vehicle not found");
    this.centreAccess.assertCanAccess(user.centreAccess, existing.centreId);

    const oldValue = { ...existing };
    const merged = this.vehicleRepo.merge(existing, {
      tankerNumber: dto.tankerNumber ?? existing.tankerNumber,
      driverName: dto.driverName ?? existing.driverName,
      driverMobile: dto.driverMobile ?? existing.driverMobile,
      capacityKg: dto.capacityKg !== undefined ? String(dto.capacityKg) : existing.capacityKg,
      status: dto.status ?? existing.status,
    });
    const updated = await this.vehicleRepo.save(merged);

    await this.audit.record({
      userId: user.id,
      centreId: existing.centreId,
      action: "VEHICLE_UPDATE",
      resourceType: "Vehicle",
      resourceId: String(id),
      oldValue,
      newValue: updated,
    });

    return updated;
  }
}
