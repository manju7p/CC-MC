import { BadRequestException, Injectable, NotFoundException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { DataSource, Repository } from "typeorm";
import { QualityParameter, ReadingSource, TransactionStatus } from "@cc-mc/shared-types";
import { MilkReceptionTransaction } from "./entities/milk-reception-transaction.entity";
import { TransactionOverride } from "./entities/transaction-override.entity";
import { ChillingCentre } from "../centres/entities/chilling-centre.entity";
import { Source } from "../sources/entities/source.entity";
import { Vehicle } from "../vehicles/entities/vehicle.entity";
import { CentreAccessService } from "../rbac/centre-access.service";
import { AuditService } from "../audit/audit.service";
import { QualityRulesService } from "../quality-rules/quality-rules.service";
import { QualityValidationService } from "./quality-validation.service";
import type { RequestUser } from "../rbac/rbac.types";
import { CreateReceptionDto } from "./dto/create-reception.dto";
import { OverrideReceptionDto } from "./dto/override-reception.dto";

const REQUIRED_PARAMETERS = [QualityParameter.FAT, QualityParameter.SNF, QualityParameter.TEMPERATURE];

@Injectable()
export class ReceptionService {
  constructor(
    @InjectRepository(MilkReceptionTransaction)
    private readonly transactionRepo: Repository<MilkReceptionTransaction>,
    @InjectRepository(ChillingCentre) private readonly centreRepo: Repository<ChillingCentre>,
    @InjectRepository(Source) private readonly sourceRepo: Repository<Source>,
    @InjectRepository(Vehicle) private readonly vehicleRepo: Repository<Vehicle>,
    private readonly dataSource: DataSource,
    private readonly centreAccess: CentreAccessService,
    private readonly audit: AuditService,
    private readonly qualityRules: QualityRulesService,
    private readonly qualityValidation: QualityValidationService,
  ) {}

  async list(user: RequestUser) {
    if (user.centreAccess.allCentres) {
      return this.transactionRepo.find({ order: { receivedAt: "DESC" }, take: 100 });
    }
    if (user.centreAccess.centreIds.length === 0) return [];
    return this.transactionRepo.find({
      where: user.centreAccess.centreIds.map((centreId) => ({ centreId })),
      order: { receivedAt: "DESC" },
      take: 100,
    });
  }

  async getById(user: RequestUser, id: number) {
    const transaction = await this.transactionRepo.findOneBy({ id });
    if (!transaction) throw new NotFoundException("Reception transaction not found");
    this.centreAccess.assertCanAccess(user.centreAccess, transaction.centreId);
    return transaction;
  }

  async create(user: RequestUser, dto: CreateReceptionDto) {
    this.centreAccess.assertCanAccess(user.centreAccess, dto.centreId);

    const [centre, source, vehicle] = await Promise.all([
      this.centreRepo.findOneBy({ id: dto.centreId }),
      this.sourceRepo.findOneBy({ id: dto.sourceId }),
      this.vehicleRepo.findOneBy({ id: dto.vehicleId }),
    ]);

    if (!centre) throw new BadRequestException("Centre not found");
    if (!source || source.centreId !== dto.centreId) {
      throw new BadRequestException("Source not found for this centre");
    }
    if (source.status !== "ACTIVE") {
      throw new BadRequestException("Source is inactive");
    }
    if (!vehicle || vehicle.centreId !== dto.centreId) {
      throw new BadRequestException("Vehicle not found for this centre");
    }
    if (vehicle.status !== "ACTIVE") {
      throw new BadRequestException("Vehicle is inactive");
    }

    const rules = await this.qualityRules.resolveRulesForCentre(dto.centreId, REQUIRED_PARAMETERS);
    const validation = this.qualityValidation.validate(
      { fat: dto.fat, snf: dto.snf, temperature: dto.temperature },
      rules,
    );

    return this.dataSource.transaction(async (manager) => {
      const repo = manager.getRepository(MilkReceptionTransaction);

      // Transaction number is assigned centrally, after the row has an id,
      // so two concurrent creations can never collide (see
      // docs/assumptions.md #transaction-numbering for why this differs
      // from the BRD's illustrative MCC-2026-000123 format).
      const created = await repo.save(
        repo.create({
          centreId: dto.centreId,
          sourceId: dto.sourceId,
          vehicleId: dto.vehicleId,
          operatorUserId: user.id,
          quantityKg: String(dto.quantityKg),
          fat: String(dto.fat),
          snf: String(dto.snf),
          temperature: String(dto.temperature),
          status: validation.status,
          readingSource: ReadingSource.MANUAL,
          reason: validation.reason,
          transactionNumber: `PENDING-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
        }),
      );

      created.transactionNumber = `${centre.code}-${created.id}`;
      const final = await repo.save(created);

      await this.audit.record(
        {
          userId: user.id,
          centreId: dto.centreId,
          action: "RECEPTION_CREATE",
          resourceType: "MilkReceptionTransaction",
          resourceId: String(final.id),
          oldValue: null,
          newValue: final,
          reason: validation.reason,
        },
        manager,
      );

      return final;
    });
  }

  async override(user: RequestUser, id: number, dto: OverrideReceptionDto) {
    const transaction = await this.transactionRepo.findOneBy({ id });
    if (!transaction) throw new NotFoundException("Reception transaction not found");
    this.centreAccess.assertCanAccess(user.centreAccess, transaction.centreId);

    if (transaction.status !== TransactionStatus.HOLD) {
      throw new BadRequestException("Only a transaction currently on HOLD can be overridden");
    }

    return this.dataSource.transaction(async (manager) => {
      const txRepo = manager.getRepository(MilkReceptionTransaction);
      const overrideRepo = manager.getRepository(TransactionOverride);

      transaction.status = dto.newStatus;
      transaction.reason = dto.reason;
      const updated = await txRepo.save(transaction);

      await overrideRepo.save(
        overrideRepo.create({
          transactionId: id,
          originalStatus: TransactionStatus.HOLD,
          newStatus: dto.newStatus,
          performedByUserId: user.id,
          reason: dto.reason,
        }),
      );

      await this.audit.record(
        {
          userId: user.id,
          centreId: transaction.centreId,
          action: "RECEPTION_OVERRIDE",
          resourceType: "MilkReceptionTransaction",
          resourceId: String(id),
          oldValue: { status: TransactionStatus.HOLD },
          newValue: { status: dto.newStatus },
          reason: dto.reason,
        },
        manager,
      );

      return updated;
    });
  }
}
