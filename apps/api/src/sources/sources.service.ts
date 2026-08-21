import { Injectable, NotFoundException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { RecordStatus } from "@cc-mc/shared-types";
import { Source } from "./entities/source.entity";
import { CentreAccessService } from "../rbac/centre-access.service";
import { AuditService } from "../audit/audit.service";
import type { RequestUser } from "../rbac/rbac.types";
import { CreateSourceDto } from "./dto/create-source.dto";
import { UpdateSourceDto } from "./dto/update-source.dto";

@Injectable()
export class SourcesService {
  constructor(
    @InjectRepository(Source) private readonly sourceRepo: Repository<Source>,
    private readonly centreAccess: CentreAccessService,
    private readonly audit: AuditService,
  ) {}

  async list(user: RequestUser) {
    if (user.centreAccess.allCentres) {
      return this.sourceRepo.find({ order: { name: "ASC" } });
    }
    if (user.centreAccess.centreIds.length === 0) return [];
    return this.sourceRepo.find({
      where: user.centreAccess.centreIds.map((centreId) => ({ centreId })),
      order: { name: "ASC" },
    });
  }

  async getById(user: RequestUser, id: number) {
    const source = await this.sourceRepo.findOneBy({ id });
    if (!source) throw new NotFoundException("Source not found");
    this.centreAccess.assertCanAccess(user.centreAccess, source.centreId);
    return source;
  }

  async create(user: RequestUser, dto: CreateSourceDto) {
    this.centreAccess.assertCanAccess(user.centreAccess, dto.centreId);

    const created = await this.sourceRepo.save(
      this.sourceRepo.create({
        centreId: dto.centreId,
        code: dto.code,
        name: dto.name,
        location: dto.location ?? null,
        contact: dto.contact ?? null,
        milkType: dto.milkType ?? null,
        status: dto.status ?? RecordStatus.ACTIVE,
      }),
    );

    await this.audit.record({
      userId: user.id,
      centreId: created.centreId,
      action: "SOURCE_CREATE",
      resourceType: "Source",
      resourceId: String(created.id),
      oldValue: null,
      newValue: created,
    });

    return created;
  }

  async update(user: RequestUser, id: number, dto: UpdateSourceDto) {
    const existing = await this.sourceRepo.findOneBy({ id });
    if (!existing) throw new NotFoundException("Source not found");
    this.centreAccess.assertCanAccess(user.centreAccess, existing.centreId);

    const oldValue = { ...existing };
    const merged = this.sourceRepo.merge(existing, {
      name: dto.name ?? existing.name,
      location: dto.location ?? existing.location,
      contact: dto.contact ?? existing.contact,
      milkType: dto.milkType ?? existing.milkType,
      status: dto.status ?? existing.status,
    });
    const updated = await this.sourceRepo.save(merged);

    await this.audit.record({
      userId: user.id,
      centreId: existing.centreId,
      action: "SOURCE_UPDATE",
      resourceType: "Source",
      resourceId: String(id),
      oldValue,
      newValue: updated,
    });

    return updated;
  }
}
