import { Injectable, UnauthorizedException } from "@nestjs/common";
import { InjectRepository } from "@nestjs/typeorm";
import { Repository } from "typeorm";
import { JwtService } from "@nestjs/jwt";
import * as bcrypt from "bcrypt";
import { RecordStatus, type AuthenticatedUser, type LoginResponse } from "@cc-mc/shared-types";
import { User } from "./entities/user.entity";
import { UserRole } from "../rbac/entities/user-role.entity";
import { UserCentreAssignment } from "../rbac/entities/user-centre-assignment.entity";
import { AuditService } from "../audit/audit.service";
import type { RequestUser } from "../rbac/rbac.types";

// Used only to keep bcrypt.compare's timing constant when no user matches
// the submitted email - see the comment in login() below. Not a real
// credential; computed once at module load, never persisted or compared
// against anything meaningful.
const DUMMY_PASSWORD_HASH = bcrypt.hashSync("no-such-user-timing-safety", 10);

@Injectable()
export class AuthService {
  constructor(
    @InjectRepository(User) private readonly userRepo: Repository<User>,
    @InjectRepository(UserRole) private readonly userRoleRepo: Repository<UserRole>,
    @InjectRepository(UserCentreAssignment)
    private readonly centreAssignmentRepo: Repository<UserCentreAssignment>,
    private readonly jwtService: JwtService,
    private readonly audit: AuditService,
  ) {}

  /**
   * Loads a user's roles, permissions, and centre access fresh from the
   * database. Called both by the login flow and by JwtStrategy on every
   * authenticated request - see rbac.types.ts for why this isn't cached.
   */
  async loadUserContext(userId: number): Promise<RequestUser | null> {
    const user = await this.userRepo.findOneBy({ id: userId });
    if (!user || user.status !== RecordStatus.ACTIVE) return null;

    const userRoles = await this.userRoleRepo.find({
      where: { userId },
      relations: { role: { rolePermissions: { permission: true } } },
    });
    const centreAssignments = await this.centreAssignmentRepo.find({ where: { userId } });

    const roleNames = userRoles.map((ur) => ur.role.name);
    const permissionCodes = Array.from(
      new Set(userRoles.flatMap((ur) => ur.role.rolePermissions.map((rp) => rp.permission.code))),
    );

    const allCentres = centreAssignments.some((a) => a.allCentres);
    const centreIds = centreAssignments
      .filter((a) => !a.allCentres && a.centreId !== null)
      .map((a) => a.centreId as number);

    return {
      id: user.id,
      email: user.email,
      fullName: user.fullName,
      roleNames,
      permissionCodes,
      centreAccess: { allCentres, centreIds },
    };
  }

  /**
   * Maps the internal RequestUser shape (roleNames/permissionCodes - see
   * rbac.types.ts) to the AuthenticatedUser shape the frontend/shared-types
   * contract actually declares (roles/permissions). login() already built
   * this mapping inline; GET /auth/me (used by the frontend to restore a
   * session on page load - see AuthContext.tsx) previously returned the raw
   * RequestUser instead, so a restored session's user.permissions was
   * undefined and hasPermission() would throw the first time anything
   * called it. Extracted here so both call sites share one mapping instead
   * of the controller re-implementing it and risking the same drift again.
   */
  toAuthenticatedUser(context: RequestUser): AuthenticatedUser {
    return {
      id: context.id,
      email: context.email,
      fullName: context.fullName,
      roles: context.roleNames,
      permissions: context.permissionCodes as AuthenticatedUser["permissions"],
      centreAccess: context.centreAccess,
    };
  }

  async login(email: string, password: string): Promise<LoginResponse> {
    const user = await this.userRepo.findOneBy({ email });

    // Always run a bcrypt comparison, even when the email doesn't match any
    // user, against a fixed dummy hash. Skipping bcrypt entirely for an
    // unknown email would make login respond measurably faster for
    // "no such user" than for "wrong password" (bcrypt is deliberately
    // slow), letting an attacker enumerate valid emails by timing alone.
    const passwordMatches = await bcrypt.compare(password, user?.passwordHash ?? DUMMY_PASSWORD_HASH);

    if (!user || user.status !== RecordStatus.ACTIVE || !passwordMatches) {
      throw new UnauthorizedException("Invalid credentials");
    }

    const context = await this.loadUserContext(user.id);
    if (!context) {
      throw new UnauthorizedException("Invalid credentials");
    }

    const accessToken = this.jwtService.sign({ sub: user.id, email: user.email });

    await this.audit.record({
      userId: user.id,
      centreId: null,
      action: "LOGIN",
      resourceType: "User",
      resourceId: String(user.id),
    });

    return {
      accessToken,
      user: this.toAuthenticatedUser(context),
    };
  }
}
