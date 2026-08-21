import { BadRequestException, ForbiddenException, Injectable, NotFoundException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { In, IsNull, Repository } from "typeorm";
import { QualityParameter } from "@cc-mc/shared-types";
import { QualityRule } from "./entities/quality-rule.entity";
import { AuditService } from "../audit/audit.service";
import type { RequestUser } from "../rbac/rbac.types";
import { UpdateQualityRuleDto } from "./dto/update-quality-rule.dto";

@Injectable()
export class QualityRulesService {
  constructor(
    @InjectRepository(QualityRule) private readonly ruleRepo: Repository<QualityRule>,
    private readonly audit: AuditService,
  ) {}

  /** Rules visible to this user: global (centreId null) plus any centre-specific rows they can access. */
  async list(user: RequestUser) {
    if (user.centreAccess.allCentres) {
      return this.ruleRepo.find({ order: { parameter: "ASC" } });
    }
    return this.ruleRepo.find({
      where: [{ centreId: IsNull() }, { centreId: In(user.centreAccess.centreIds.length ? user.centreAccess.centreIds : [-1]) }],
      order: { parameter: "ASC" },
    });
  }

  async update(user: RequestUser, id: number, dto: UpdateQualityRuleDto) {
    const existing = await this.ruleRepo.findOneBy({ id });
    if (!existing) throw new NotFoundException("Quality rule not found");

    // A global rule (centreId null) affects every centre, so only a user
    // with organization-wide (allCentres) access may edit it. A
    // centre-specific rule follows the normal centre-scope check.
    if (existing.centreId === null) {
      if (!user.centreAccess.allCentres) {
        throw new ForbiddenException("Only an organization-wide user may edit a global quality rule");
      }
    } else if (!user.centreAccess.allCentres && !user.centreAccess.centreIds.includes(existing.centreId)) {
      throw new ForbiddenException(`No access to centre ${existing.centreId}`);
    }

    if (dto.minValue >= dto.maxValue) {
      throw new BadRequestException("minValue must be less than maxValue");
    }

    const oldValue = { ...existing };
    const updated = await this.ruleRepo.save(
      this.ruleRepo.merge(existing, { minValue: String(dto.minValue), maxValue: String(dto.maxValue) }),
    );

    await this.audit.record({
      userId: user.id,
      centreId: existing.centreId,
      action: "QUALITY_RULE_UPDATE",
      resourceType: "QualityRule",
      resourceId: String(id),
      oldValue,
      newValue: updated,
    });

    return updated;
  }

  /**
   * Resolves the applicable min/max for each required parameter for a given
   * centre: a centre-specific rule overrides the global default. Throws if
   * a required parameter has no rule configured at all - reception cannot
   * proceed without a complete rule set (fail closed, not a silent default).
   */
  async resolveRulesForCentre(
    centreId: number,
    parameters: QualityParameter[],
  ): Promise<Map<QualityParameter, { minValue: number; maxValue: number }>> {
    const rules = await this.ruleRepo.find({
      where: [{ centreId: IsNull() }, { centreId }],
    });

    const resolved = new Map<QualityParameter, { minValue: number; maxValue: number }>();
    for (const param of parameters) {
      const centreSpecific = rules.find((r) => r.parameter === param && r.centreId === centreId);
      const global = rules.find((r) => r.parameter === param && r.centreId === null);
      const chosen = centreSpecific ?? global;
      if (!chosen) {
        throw new BadRequestException(`No quality rule configured for ${param} (centre ${centreId})`);
      }
      resolved.set(param, { minValue: Number(chosen.minValue), maxValue: Number(chosen.maxValue) });
    }
    return resolved;
  }
}
