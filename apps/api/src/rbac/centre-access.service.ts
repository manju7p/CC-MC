import { ForbiddenException, Injectable } from "@nestjs/common";
import type { CentreAccess } from "./rbac.types";

/**
 * Centre-scope enforcement (Engineering Rule 2 / "Centre Scoping" section).
 *
 * This is intentionally a small set of plain functions, not a query-builder
 * abstraction - every controller/service that touches centre-scoped data
 * calls one of these explicitly, so the check is visible at the call site
 * rather than hidden behind a generic mechanism.
 */
@Injectable()
export class CentreAccessService {
  /** Throws if the user is not allowed to access the given centre. */
  assertCanAccess(access: CentreAccess, centreId: number): void {
    if (access.allCentres) return;
    if (access.centreIds.includes(centreId)) return;
    throw new ForbiddenException(`No access to centre ${centreId}`);
  }
}
