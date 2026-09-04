import { BadRequestException, ConflictException, Injectable, NotFoundException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { DataSource, EntityManager, Repository } from "typeorm";
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

/**
 * The fields create() actually compares to decide "same logical payload"
 * for an idempotent retry (Checkpoint 5). Deliberately every business
 * field the caller controls - centreId/sourceId/vehicleId identify WHAT
 * was received, quantityKg/fat/snf/temperature identify the reading
 * itself. operatorUserId is NOT compared: two retries of the same
 * physical capture, from the gateway's perspective, always carry the
 * gateway's own service-account user id anyway (see the gateway
 * cloud-auth design), so it adds no discriminating signal and comparing
 * it would only risk a false conflict if that ever changed.
 */
type ReceptionPayloadFields = Pick<
  CreateReceptionDto,
  "centreId" | "sourceId" | "vehicleId" | "quantityKg" | "fat" | "snf" | "temperature"
>;

/**
 * Additive response shape for POST /reception (Checkpoint 5): every
 * existing field of MilkReceptionTransaction, unchanged, plus `outcome` -
 * "created" for a genuinely new row, "duplicate" for an idempotent retry
 * that returned an already-existing row. Both are HTTP 201 (Nest's POST
 * default, unchanged) - `outcome` is how a caller that cares (the
 * gateway's HttpCloudClient) tells them apart; the existing web UI simply
 * never reads this field, exactly like it never reads any other field it
 * doesn't use. A genuine payload conflict is NOT part of this type - see
 * create()'s ConflictException path.
 */
export type ReceptionCreateResult = MilkReceptionTransaction & { outcome: "created" | "duplicate" };

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

      // Checkpoint 5: a caller that supplied localIdempotencyKey (the
      // Local Device Gateway, always; the web UI, never) gets the
      // idempotent-retry-safe path. Every other field of this method is
      // untouched from Checkpoint 2 - the two paths share the same
      // validation, quality-rule resolution, and transaction-number
      // assignment; they only differ in how the row gets inserted.
      if (dto.localIdempotencyKey) {
        return this.createIdempotent(manager, repo, user, dto, centre, validation);
      }

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
          localIdempotencyKey: null,
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

      return { ...final, outcome: "created" as const };
    });
  }

  /**
   * The idempotent-retry-safe insert path (Checkpoint 5), used only when
   * the caller supplied a localIdempotencyKey. Uses the ALREADY-EXISTING
   * nullable-unique constraint on milk_reception_transactions.localIdempotencyKey
   * (see the InitSchema migration - this checkpoint adds no schema change)
   * as the actual correctness boundary, via `INSERT ... ON CONFLICT
   * ("localIdempotencyKey") DO NOTHING` - not an application-level
   * pre-check-then-insert, which would leave a race window between the
   * check and the insert. Postgres itself resolves the race: if two
   * transactions race to insert the same key, the SECOND one's INSERT
   * statement blocks until the FIRST one commits or rolls back; once it
   * unblocks, its own ON CONFLICT clause correctly sees whether the key
   * now exists (and skips) or doesn't (and proceeds) - see
   * docs/gateway-architecture.md's cloud-sync section and
   * test/reception-idempotency.e2e-spec.ts's concurrent test for the
   * proof. This one query is also why the manual conflict-detection path
   * below never needs its own locking: by the time it runs, Postgres has
   * already guaranteed the row it reads is the single, final, committed
   * winner.
   */
  private async createIdempotent(
    manager: EntityManager,
    repo: Repository<MilkReceptionTransaction>,
    user: RequestUser,
    dto: CreateReceptionDto,
    centre: ChillingCentre,
    validation: { status: TransactionStatus; reason: string | null },
  ): Promise<ReceptionCreateResult> {
    const insertResult = await repo
      .createQueryBuilder()
      .insert()
      .into(MilkReceptionTransaction)
      .values({
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
        localIdempotencyKey: dto.localIdempotencyKey,
      })
      .onConflict(`("localIdempotencyKey") DO NOTHING`)
      .returning(["id"])
      .execute();

    const insertedId: number | undefined = (insertResult.raw as Array<{ id: number }> | undefined)?.[0]?.id;

    if (insertedId !== undefined) {
      // Our INSERT actually landed a new row - finish exactly like the
      // non-idempotent path: assign the real transactionNumber now that
      // an id exists, save, and audit. Fetched fresh (not built from the
      // insert values) so `final` reflects exactly what the database has,
      // matching the non-idempotent path's behavior.
      const created = await repo.findOneByOrFail({ id: insertedId });
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

      return { ...final, outcome: "created" as const };
    }

    // Our INSERT was skipped - a row with this localIdempotencyKey already
    // exists and (per the comment above) is guaranteed committed and
    // visible to us right now. Compare payloads: same payload is a safe,
    // audit-free replay; a different payload is a caller bug surfaced
    // loudly (see ReceptionPayloadFields's doc comment) - either way,
    // nothing new is written, and NO second audit record is created.
    const existing = await repo.findOneByOrFail({ localIdempotencyKey: dto.localIdempotencyKey });
    const conflicts = this.findPayloadConflicts(existing, dto);

    if (conflicts.length > 0) {
      throw new ConflictException({
        message:
          `localIdempotencyKey "${dto.localIdempotencyKey}" was already used for a transaction with ` +
          `different data (conflicting field(s): ${conflicts.join(", ")}). Retrying with the same ` +
          `idempotency key must resubmit the SAME logical payload - this looks like a caller bug, not ` +
          `a legitimate retry.`,
        conflictingFields: conflicts,
        existingTransactionId: existing.id,
      });
    }

    return { ...existing, outcome: "duplicate" as const };
  }

  /**
   * Exact-value comparison between an already-persisted transaction and an
   * incoming request claiming the same localIdempotencyKey - mirrors the
   * gateway's own findPayloadConflicts() (apps/gateway/src/storage/
   * sqlite-local-storage.ts) so both halves of the same idempotency
   * contract agree on what "the same payload" means. Numeric columns are
   * compared via Number(...) rather than string equality because they are
   * persisted as fixed-precision decimal strings (e.g. "45.50") - a
   * caller resubmitting `45.5` must not be treated as a conflict merely
   * because of trailing-zero formatting.
   */
  private findPayloadConflicts(existing: MilkReceptionTransaction, incoming: ReceptionPayloadFields): string[] {
    const conflicts: string[] = [];
    if (existing.centreId !== incoming.centreId) conflicts.push("centreId");
    if (existing.sourceId !== incoming.sourceId) conflicts.push("sourceId");
    if (existing.vehicleId !== incoming.vehicleId) conflicts.push("vehicleId");
    if (Number(existing.quantityKg) !== incoming.quantityKg) conflicts.push("quantityKg");
    if (Number(existing.fat) !== incoming.fat) conflicts.push("fat");
    if (Number(existing.snf) !== incoming.snf) conflicts.push("snf");
    if (Number(existing.temperature) !== incoming.temperature) conflicts.push("temperature");
    return conflicts;
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
