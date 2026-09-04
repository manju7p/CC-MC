import { Body, Controller, Get, Post, UseGuards } from "@nestjs/common";
import type { AuthenticatedUser, LoginResponse } from "@cc-mc/shared-types";
import { AuthService } from "./auth.service";
import { LoginDto } from "./dto/login.dto";
import { JwtAuthGuard } from "./jwt-auth.guard";
import { CurrentUser } from "../common/current-user.decorator";
import type { RequestUser } from "../rbac/rbac.types";

@Controller("auth")
export class AuthController {
  constructor(private readonly authService: AuthService) {}

  @Post("login")
  async login(@Body() dto: LoginDto): Promise<LoginResponse> {
    return this.authService.login(dto.email, dto.password);
  }

  /**
   * Returns the current user's context (roles/permissions/centre access), in
   * the same AuthenticatedUser shape as POST /auth/login's `user` field - so
   * the frontend can restore a session on page load (see AuthContext.tsx)
   * using the exact same type it gets from login, instead of the internal
   * RequestUser shape (roleNames/permissionCodes) this previously returned
   * unmapped, which left a restored session's user.permissions undefined.
   * Not itself a security control (see Rule 2: frontend checks are UX only).
   */
  @Get("me")
  @UseGuards(JwtAuthGuard)
  me(@CurrentUser() user: RequestUser): AuthenticatedUser {
    return this.authService.toAuthenticatedUser(user);
  }
}
