import { Injectable } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { TransactionStatus } from "@cc-mc/shared-types";
import { MilkReceptionTransaction } from "../reception/entities/milk-reception-transaction.entity";
import { CentreAccessService } from "../rbac/centre-access.service";
import type { CentreAccess } from "../rbac/rbac.types";

// MVP assumption (docs/assumptions.md #dashboard-day-boundary): "today" is
// computed as the IST (UTC+5:30) calendar day, since the BRD's deployment
// context is Indian dairy chilling centres, not the server's own timezone.
// Hardcoded rather than pulling in a timezone library, per Rule 4.
const IST_OFFSET_MS = 5.5 * 60 * 60 * 1000;

function istDayBoundsUtc(now: Date): { start: Date; end: Date; istDateLabel: string } {
  const shifted = new Date(now.getTime() + IST_OFFSET_MS);
  const istMidnightAsUtc = Date.UTC(shifted.getUTCFullYear(), shifted.getUTCMonth(), shifted.getUTCDate());
  const start = new Date(istMidnightAsUtc - IST_OFFSET_MS);
  const end = new Date(start.getTime() + 24 * 60 * 60 * 1000);
  // `start` is a UTC instant, so its own ISO date can land on the previous
  // UTC calendar day even though it marks the start of *today* in IST
  // (e.g. IST midnight on the 20th is 18:30 UTC on the 19th). The label
  // shown to users must be the IST calendar date, so it's derived from the
  // IST-shifted clock face, not from `start` itself.
  const istDateLabel = shifted.toISOString().slice(0, 10);
  return { start, end, istDateLabel };
}

@Injectable()
export class DashboardService {
  constructor(
    @InjectRepository(MilkReceptionTransaction)
    private readonly transactionRepo: Repository<MilkReceptionTransaction>,
    private readonly centreAccess: CentreAccessService,
  ) {}

  async summary(access: CentreAccess, centreIdFilter?: number) {
    if (centreIdFilter !== undefined) {
      this.centreAccess.assertCanAccess(access, centreIdFilter);
    }

    const { start, end, istDateLabel } = istDayBoundsUtc(new Date());

    const qb = this.transactionRepo
      .createQueryBuilder("t")
      .select("t.status", "status")
      .addSelect("COUNT(*)", "count")
      .where("t.receivedAt >= :start AND t.receivedAt < :end", { start, end })
      .groupBy("t.status");

    if (centreIdFilter !== undefined) {
      qb.andWhere("t.centreId = :centreId", { centreId: centreIdFilter });
    } else if (!access.allCentres) {
      qb.andWhere("t.centreId IN (:...centreIds)", {
        centreIds: access.centreIds.length ? access.centreIds : [-1],
      });
    }

    const rows = await qb.getRawMany<{ status: TransactionStatus; count: string }>();
    const countFor = (status: TransactionStatus) => Number(rows.find((r) => r.status === status)?.count ?? 0);

    const accepted = countFor(TransactionStatus.ACCEPTED);
    const rejected = countFor(TransactionStatus.REJECTED);
    const hold = countFor(TransactionStatus.HOLD);

    return {
      date: istDateLabel,
      centreIds: centreIdFilter !== undefined ? [centreIdFilter] : access.centreIds,
      totalTransactions: accepted + rejected + hold,
      accepted,
      rejected,
      hold,
    };
  }
}
