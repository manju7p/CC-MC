import { SetMetadata } from "@nestjs/common";
import type { PermissionCode } from "@cc-mc/shared-types";

export const PERMISSION_KEY = "requiredPermission";

/**
 * Marks a controller method as requiring the given permission code.
 * Enforced by PermissionGuard - see rbac/permission.guard.ts.
 *
 * This is deliberately a single required permission per endpoint, not a
 * boolean expression language - per Rule 2/Rule 4, fine-grained permission
 * codes are sufficient for the MVP and a generic policy engine is explicitly
 * out of scope.
 */
export const RequirePermission = (code: PermissionCode) => SetMetadata(PERMISSION_KEY, code);
