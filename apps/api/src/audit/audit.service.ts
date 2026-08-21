import { Injectable } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { EntityManager, Repository } from "typeorm";
import { AuditLog } from "./entities/audit-log.entity";

export interface RecordAuditInput {
  userId: number | null;
  centreId: number | null;
  action: string;
  resourceType: string;
  resourceId: string;
  oldValue?: unknown;
  newValue?: unknown;
  reason?: string | null;
}

/**
 * Single write path for audit records (BRD Section 48 / tonight's scope).
 * Every module that mutates something audit-worthy calls
 * `AuditService.record(...)` explicitly - there is no generic interceptor
 * that infers audit content automatically, because "old value / new value"
 * needs real domain knowledge of what changed (Rule 4: no premature
 * generic abstraction).
 *
 * Accepts an optional EntityManager (from a running TypeORM transaction)
 * so callers can write the audit record atomically with the business
 * mutation it describes.
 */
@Injectable()
export class AuditService {
  constructor(@InjectRepository(AuditLog) private readonly auditRepo: Repository<AuditLog>) {}

  async record(input: RecordAuditInput, manager?: EntityManager): Promise<void> {
    const repo = manager ? manager.getRepository(AuditLog) : this.auditRepo;
    const entry = repo.create({
      userId: input.userId,
      centreId: input.centreId,
      action: input.action,
      resourceType: input.resourceType,
      resourceId: input.resourceId,
      oldValue: input.oldValue ?? null,
      newValue: input.newValue ?? null,
      reason: input.reason ?? null,
    });
    await repo.save(entry);
  }
}
