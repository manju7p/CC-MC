import { CanActivate, ExecutionContext, ForbiddenException, Injectable } from "@nestjs/common";
import { Reflector } from "@nestjs/core";
import type { PermissionCode } from "@cc-mc/shared-types";
import { PERMISSION_KEY } from "./permissions.decorator";
import type { RequestUser } from "./rbac.types";

/**
 * Backend-authoritative permission check (Engineering Rule 2). This guard
 * runs after JwtAuthGuard has attached `req.user`. It only checks
 * "does this user hold this permission" - centre scope is a separate,
 * explicit check performed in each controller/service via
 * CentreAccessService, because the target centre for a request cannot be
 * inferred generically (it might come from the request body on create, or
 * from the fetched entity on read/update).
 */
@Injectable()
export class PermissionGuard implements CanActivate {
  constructor(private readonly reflector: Reflector) {}

  canActivate(context: ExecutionContext): boolean {
    const required = this.reflector.get<PermissionCode | undefined>(PERMISSION_KEY, context.getHandler());

    if (!required) {
      // No @RequirePermission on this handler - fail closed would break
      // unrelated public/utility endpoints, so instead every protected
      // controller in this codebase MUST declare a permission explicitly.
      // Absence of the decorator is a programming error, not "allow".
      throw new ForbiddenException("Endpoint is missing a required permission declaration");
    }

    const request = context.switchToHttp().getRequest();
    const user: RequestUser | undefined = request.user;

    if (!user) {
      throw new ForbiddenException("No authenticated user on request");
    }

    if (!user.permissionCodes.includes(required)) {
      throw new ForbiddenException(`Missing required permission: ${required}`);
    }

    return true;
  }
}
