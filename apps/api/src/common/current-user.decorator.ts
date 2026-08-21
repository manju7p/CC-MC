import { createParamDecorator, ExecutionContext } from "@nestjs/common";
import type { RequestUser } from "../rbac/rbac.types";

export const CurrentUser = createParamDecorator((_data: unknown, ctx: ExecutionContext): RequestUser => {
  const request = ctx.switchToHttp().getRequest();
  return request.user;
});
